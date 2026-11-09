// Package agentwire exposes the small SSH agent surface used by opssh.
package agentwire

import (
	"context"
	"encoding/binary"
	"errors"
	"io"
	"net"
	"time"

	"golang.org/x/crypto/ssh"
)

const MaxFrame = 256 * 1024

type Identity struct {
	Blob    []byte
	Comment string
}

type Handler interface {
	List() []Identity
	Sign(context.Context, []byte, []byte, uint32, func(*ssh.Signature) error) error
}

var errFrame = errors.New("invalid agent frame")

func Serve(ctx context.Context, conn net.Conn, handler Handler) error {
	var header [4]byte
	for {
		if err := ctx.Err(); err != nil {
			return err
		}
		if err := conn.SetReadDeadline(time.Now().Add(2 * time.Minute)); err != nil {
			return err
		}
		if _, err := io.ReadFull(conn, header[:]); err != nil {
			return err
		}
		size := binary.BigEndian.Uint32(header[:])
		if size == 0 || size > MaxFrame {
			return errFrame
		}
		body := make([]byte, size)
		if _, err := io.ReadFull(conn, body); err != nil {
			return err
		}
		if err := dispatch(ctx, conn, handler, body); err != nil {
			return err
		}
	}
}

func dispatch(ctx context.Context, conn net.Conn, handler Handler, body []byte) error {
	switch body[0] {
	case 11: // SSH_AGENTC_REQUEST_IDENTITIES
		if len(body) == 1 {
			ids := handler.List()
			reply := make([]byte, 5)
			reply[0] = 12
			binary.BigEndian.PutUint32(reply[1:], uint32(len(ids)))
			for _, id := range ids {
				reply = append(reply, ssh.Marshal(id)...)
			}
			if len(reply) <= MaxFrame {
				return writeReply(conn, reply)
			}
		}
	case 13: // SSH_AGENTC_SIGN_REQUEST
		var req struct {
			Key, Data []byte
			Flags     uint32
		}
		if ssh.Unmarshal(body[1:], &req) != nil || len(req.Key) == 0 {
			break
		}
		var wrote bool
		var writeErr error
		signErr := handler.Sign(ctx, req.Key, req.Data, req.Flags, func(sig *ssh.Signature) error {
			if err := ctx.Err(); err != nil {
				return err
			}
			wrote = true
			writeErr = writeReply(conn, append([]byte{14}, ssh.Marshal(struct{ Signature []byte }{ssh.Marshal(sig)})...))
			return writeErr
		})
		if wrote {
			return writeErr
		}
		if signErr != nil && ctx.Err() != nil {
			return ctx.Err()
		}
	}
	// Unsupported operations, including extensions and identity mutation, are rejected before decoding.
	return writeReply(conn, []byte{5})
}

func writeReply(conn net.Conn, body []byte) error {
	if err := conn.SetWriteDeadline(time.Now().Add(time.Second)); err != nil {
		return err
	}
	frame := make([]byte, 4, 4+len(body))
	binary.BigEndian.PutUint32(frame, uint32(len(body)))
	frame = append(frame, body...)
	n, err := conn.Write(frame)
	if err == nil && n != len(frame) {
		return io.ErrShortWrite
	}
	return err
}
