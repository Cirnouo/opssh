package control

import (
	"bytes"
	"context"
	"encoding/binary"
	"errors"
	"io"
	"net"
	"testing"
	"time"
)

func TestFrameLimitAndVersion(t *testing.T) {
	var frame bytes.Buffer
	if err := Write(&frame, Event("hello", "", map[string]string{"pipe": "local"})); err != nil {
		t.Fatal(err)
	}
	msg, err := Read(&frame)
	if err != nil || msg.Version != 1 || msg.Type != "hello" {
		t.Fatalf("message %v, error %v", msg, err)
	}
	var h [4]byte
	binary.BigEndian.PutUint32(h[:], 256*1024+1)
	if _, err := Read(bytes.NewReader(h[:])); err == nil {
		t.Fatal("accepted excessive control payload length")
	}
	if err := Write(&frame, Message{Version: 2, Type: "decision"}); err == nil {
		t.Fatal("accepted a different protocol version")
	}
}

func TestUnrelatedAndReplayDecisionsCannotGrantNewRequest(t *testing.T) {
	server, gui := net.Pipe()
	t.Cleanup(func() { gui.Close(); server.Close() })
	gui.SetDeadline(time.Now().Add(3 * time.Second))
	ch := New(server)
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	go ch.Run(ctx, func(context.Context, Message) Result { return Result{OK: true} }, nil)
	prompt := func() <-chan Decision {
		out := make(chan Decision, 1)
		go func() {
			d, _ := ch.Prompt(ctx, map[string]string{"name": "test"})
			out <- d
		}()
		return out
	}
	first := prompt()
	req, err := Read(gui)
	if err != nil || req.Type != "approval" {
		t.Fatalf("%v %v", req, err)
	}
	if err := Write(gui, Event("decision", "forged", Decision{Scope: "once"})); err != nil {
		t.Fatal(err)
	}
	if err := Write(gui, Event("decision", req.ID, Decision{Scope: "deny"})); err != nil {
		t.Fatal(err)
	}
	if end, err := Read(gui); err != nil || end.Type != "approval-ended" {
		t.Fatalf("%v %v", end, err)
	}
	if (<-first).Scope == "once" {
		t.Fatal("foreign response granted request")
	}
	second := prompt()
	req2, err := Read(gui)
	if err != nil || req2.ID == req.ID {
		t.Fatalf("%v %v", req2, err)
	}
	if err := Write(gui, Event("decision", req.ID, Decision{Scope: "once"})); err != nil {
		t.Fatal(err)
	}
	if err := Write(gui, Event("decision", req2.ID, Decision{Scope: "app", Candidate: 1})); err != nil {
		t.Fatal(err)
	}
	if _, err := Read(gui); err != nil {
		t.Fatal(err)
	}
	if got := <-second; got.Scope != "app" || got.Candidate != 1 {
		t.Fatalf("decision: %+v", got)
	}
	for _, scope := range []string{"all", "unavailable"} {
		out := prompt()
		request, err := Read(gui)
		if err != nil || request.Type != "approval" {
			t.Fatalf("%v %v", request, err)
		}
		if err := Write(gui, Event("decision", request.ID, Decision{Scope: scope})); err != nil {
			t.Fatal(err)
		}
		if end, err := Read(gui); err != nil || end.Type != "approval-ended" || end.ID != request.ID {
			t.Fatalf("%v %v", end, err)
		}
		if got := <-out; got.Scope != scope || got.Candidate != 0 {
			t.Fatalf("decision: %+v", got)
		}
	}
}

func TestDisconnectUnblocksPendingPrompt(t *testing.T) {
	server, gui := net.Pipe()
	t.Cleanup(func() { gui.Close(); server.Close() })
	gui.SetDeadline(time.Now().Add(2 * time.Second))
	ch := New(server)
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	go ch.Run(ctx, func(context.Context, Message) Result { return Result{OK: true} }, nil)
	out := make(chan error, 1)
	go func() { _, err := ch.Prompt(ctx, struct{}{}); out <- err }()
	if _, err := Read(gui); err != nil {
		t.Fatal(err)
	}
	gui.Close()
	if err := <-out; !errors.Is(err, ErrDisconnected) {
		t.Fatalf("prompt error: %v", err)
	}
}

type failedRetirement struct {
	net.Conn
	writes int
}

func (c *failedRetirement) Write(p []byte) (int, error) {
	c.writes++
	if c.writes == 2 {
		return 0, io.ErrClosedPipe
	}
	return c.Conn.Write(p)
}

func TestFailedRetirementCannotReturnApproval(t *testing.T) {
	server, gui := net.Pipe()
	t.Cleanup(func() { gui.Close(); server.Close() })
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	gui.SetDeadline(time.Now().Add(3 * time.Second))
	ch := New(&failedRetirement{Conn: server})
	go ch.Run(ctx, func(context.Context, Message) Result { return Result{OK: true} }, nil)
	out := make(chan error, 1)
	go func() { _, err := ch.Prompt(ctx, struct{}{}); out <- err }()
	request, err := Read(gui)
	if err != nil {
		t.Fatal(err)
	}
	if err := Write(gui, Event("decision", request.ID, Decision{Scope: "once"})); err != nil {
		t.Fatal(err)
	}
	if err := <-out; !errors.Is(err, ErrDisconnected) {
		t.Fatalf("observed disconnect returned a grant: %v", err)
	}
}
