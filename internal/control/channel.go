// Package control implements the private, versioned tray channel.
package control

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"

	"sync"
	"time"
)

const maxFrame = 256 * 1024
const maxPending = 16
const maxCommands = 8

var (
	ErrDisconnected = errors.New("tray disconnected")
	errMalformed    = errors.New("invalid control message")
	errBusy         = errors.New("approval queue is full")
)

type Message struct {
	Version int             `json:"v"`
	Type    string          `json:"type"`
	ID      string          `json:"id,omitempty"`
	Body    json.RawMessage `json:"body,omitempty"`
}

type Result struct {
	OK              bool   `json:"ok"`
	Error           string `json:"error,omitempty"`
	ErrorCode       string `json:"errorCode,omitempty"`
	State           any    `json:"state,omitempty"`
	Data            any    `json:"data,omitempty"`
	RestartRequired bool   `json:"restartRequired,omitempty"`
}

type Decision struct {
	Scope     string `json:"scope"`
	Candidate int    `json:"candidate"`
}

func Event(kind, id string, body any) Message {
	msg := Message{Version: 1, Type: kind, ID: id}
	if body != nil {
		msg.Body, _ = json.Marshal(body)
	}
	return msg
}

func DecodeBody(msg Message, dest any) error {
	return decode(msg.Body, dest)
}

func decode(data []byte, dest any) error {
	if !json.Valid(data) || len(data) == 0 || bytes.TrimSpace(data)[0] != '{' {
		return errMalformed
	}
	d := json.NewDecoder(bytes.NewReader(data))
	d.DisallowUnknownFields()
	if d.Decode(dest) != nil {
		return errMalformed
	}
	return nil
}

func (m Message) valid() bool {
	return m.Version == 1 && len(m.Type) > 0 && len(m.Type) <= 32 && len(m.ID) <= 64
}

func Read(r io.Reader) (Message, error) {
	var length [4]byte
	if _, err := io.ReadFull(r, length[:]); err != nil {
		return Message{}, err
	}
	return readBody(r, length)
}

func readBody(r io.Reader, length [4]byte) (Message, error) {
	size := binary.BigEndian.Uint32(length[:])
	if size == 0 || size > maxFrame {
		return Message{}, errMalformed
	}
	buf := make([]byte, size)
	if _, err := io.ReadFull(r, buf); err != nil {
		return Message{}, err
	}
	var msg Message
	if decode(buf, &msg) != nil || !msg.valid() {
		return Message{}, errMalformed
	}
	return msg, nil
}

func Write(w io.Writer, m Message) error {
	if !m.valid() {
		return errMalformed
	}
	b, err := json.Marshal(m)
	if err != nil || len(b) > maxFrame {
		return errMalformed
	}
	frame := make([]byte, 4, 4+len(b))
	binary.BigEndian.PutUint32(frame, uint32(len(b)))
	frame = append(frame, b...)
	n, err := w.Write(frame)
	if err == nil && n != len(frame) {
		return io.ErrShortWrite
	}
	return err
}

type transport interface {
	io.ReadWriteCloser
	SetReadDeadline(time.Time) error
	SetWriteDeadline(time.Time) error
}

type Channel struct {
	conn    transport
	done    chan struct{}
	close   sync.Once
	writes  sync.Mutex
	mu      sync.Mutex
	pending map[string]chan Decision
}

func New(conn transport) *Channel {
	return &Channel{conn: conn, done: make(chan struct{}), pending: make(map[string]chan Decision)}
}

func (c *Channel) Close() {
	c.close.Do(func() { close(c.done); c.conn.Close() })
}

func (c *Channel) Done() <-chan struct{} { return c.done }

func (c *Channel) Pending() int {
	c.mu.Lock()
	defer c.mu.Unlock()
	return len(c.pending)
}

func (c *Channel) Send(m Message) error {
	c.writes.Lock()
	defer c.writes.Unlock()
	select {
	case <-c.done:
		return ErrDisconnected
	default:
	}
	if err := c.conn.SetWriteDeadline(time.Now().Add(time.Second)); err != nil {
		c.Close()
		return err
	}
	if err := Write(c.conn, m); err != nil {
		c.Close()
		return err
	}
	return nil
}

func (c *Channel) Prompt(ctx context.Context, body any) (Decision, error) {
	if err := ctx.Err(); err != nil {
		return Decision{}, err
	}
	var token [16]byte
	if _, err := rand.Read(token[:]); err != nil {
		return Decision{}, err
	}
	id := hex.EncodeToString(token[:])
	reply := make(chan Decision, 1)
	c.mu.Lock()
	if len(c.pending) >= maxPending {
		c.mu.Unlock()
		return Decision{}, errBusy
	}
	c.pending[id] = reply
	c.mu.Unlock()
	defer func() {
		c.mu.Lock()
		delete(c.pending, id)
		c.mu.Unlock()
	}()
	if err := c.Send(Event("approval", id, body)); err != nil {
		return Decision{}, ErrDisconnected
	}
	select {
	case <-c.done:
		return Decision{}, ErrDisconnected
	case <-ctx.Done():
		_ = c.Send(Event("approval-ended", id, nil))
		return Decision{}, ctx.Err()
	case d := <-reply:
		if err := c.Send(Event("approval-ended", id, nil)); err != nil {
			return Decision{}, ErrDisconnected
		}
		if err := ctx.Err(); err != nil {
			return Decision{}, err
		}
		select {
		case <-c.done:
			return Decision{}, ErrDisconnected
		default:
		}
		return d, nil
	}
}

func (c *Channel) decide(msg Message) {
	var decision Decision
	if msg.ID == "" || DecodeBody(msg, &decision) != nil {
		return
	}
	switch decision.Scope {
	case "once", "app", "all", "deny", "unavailable":
	default:
		return
	}
	c.mu.Lock()
	response := c.pending[msg.ID]
	delete(c.pending, msg.ID)
	c.mu.Unlock()
	if response != nil {
		response <- decision
	}
}

func (c *Channel) read() (Message, error) {
	var length [4]byte
	if err := c.conn.SetReadDeadline(time.Time{}); err != nil {
		return Message{}, err
	}
	if _, err := io.ReadFull(c.conn, length[:1]); err != nil {
		return Message{}, err
	}
	if err := c.conn.SetReadDeadline(time.Now().Add(3 * time.Second)); err != nil {
		return Message{}, err
	}
	if _, err := io.ReadFull(c.conn, length[1:]); err != nil {
		return Message{}, err
	}
	return readBody(c.conn, length)
}

// Run reads decisions and bounded safety operations without waiting for provider work.
func (c *Channel) Run(ctx context.Context, handle func(context.Context, Message) Result, priority func(Message) (Result, bool)) error {
	ctx, cancel := context.WithCancel(ctx)
	stop := context.AfterFunc(ctx, c.Close)
	workerDone := make(chan struct{})
	defer func() {
		cancel()
		stop()
		c.Close()
		<-workerDone
	}()
	commands := make(chan Message, maxCommands)
	go func() {
		defer close(workerDone)
		for {
			select {
			case <-ctx.Done():
				return
			case <-c.done:
				return
			case m := <-commands:
				if ctx.Err() != nil {
					return
				}
				if err := c.Send(Event("result", m.ID, handle(ctx, m))); err != nil {
					return
				}
			}
		}
	}()
	for {
		msg, err := c.read()
		if err != nil {
			return err
		}
		if msg.ID == "" {
			return errMalformed
		}
		switch msg.Type {
		case "decision":
			c.decide(msg)
		case "shutdown":
			if priority != nil {
				priority(Message{Type: "lock"})
			}
			return c.Send(Event("result", msg.ID, Result{OK: true}))
		default:
			if priority != nil {
				if result, handled := priority(msg); handled {
					if err := c.Send(Event("result", msg.ID, result)); err != nil {
						return err
					}
					continue
				}
			}
			select {
			case commands <- msg:
			default:
				if err := c.Send(Event("result", msg.ID, Result{
					Error: "Too many pending commands.", ErrorCode: "too-many-commands",
				})); err != nil {
					return err
				}
			}
		}
	}
}
