package agentwire

import (
	"context"
	"encoding/binary"
	"io"
	"net"
	"testing"
	"time"

	"golang.org/x/crypto/ssh"
)

type fixture struct{ signed int }

func (*fixture) List() []Identity {
	return []Identity{{Blob: []byte("public"), Comment: "test"}}
}
func (f *fixture) Sign(_ context.Context, blob, data []byte, flags uint32, send func(*ssh.Signature) error) error {
	f.signed++
	if string(blob) != "public" || string(data) != "test-message" || flags != 0 {
		panic("wire request data changed")
	}
	return send(&ssh.Signature{Format: "ssh-ed25519", Blob: []byte("test-signature")})
}

func TestEnumerateAndSignUsingAgentFrames(t *testing.T) {
	client, server := net.Pipe()
	t.Cleanup(func() { client.Close(); server.Close() })
	client.SetDeadline(time.Now().Add(3 * time.Second))
	f := new(fixture)
	done := make(chan error, 1)
	go func() { done <- Serve(context.Background(), server, f) }()
	reply := exchange(t, client, []byte{11})
	want := "\x0c\x00\x00\x00\x01\x00\x00\x00\x06public\x00\x00\x00\x04test"
	if string(reply) != want {
		t.Fatalf("identity answer %x", reply)
	}
	req := append([]byte{13}, ssh.Marshal(struct {
		Key, Data string
		Flags     uint32
	}{"public", "test-message", 0})...)
	reply = exchange(t, client, req)
	var signed struct{ Signature []byte }
	var sig ssh.Signature
	if len(reply) == 0 || reply[0] != 14 || ssh.Unmarshal(reply[1:], &signed) != nil ||
		ssh.Unmarshal(signed.Signature, &sig) != nil || string(sig.Blob) != "test-signature" {
		t.Fatalf("invalid sign response: %x", reply)
	}
	client.Close()
	<-done
	if f.signed != 1 {
		t.Fatalf("got %d signing calls", f.signed)
	}
}

func TestRejectedAndOversizedFramesNeverCallSigner(t *testing.T) {
	client, server := net.Pipe()
	t.Cleanup(func() { client.Close(); server.Close() })
	client.SetDeadline(time.Now().Add(3 * time.Second))
	f := new(fixture)
	done := make(chan error, 1)
	go func() { done <- Serve(context.Background(), server, f); server.Close() }()
	for _, bad := range [][]byte{{13, 0}, {11, 1}, {17, 0, 0, 0, 99}, {27, 0}} {
		if got := exchange(t, client, bad); string(got) != "\x05" {
			t.Fatalf("unsupported request %x got %x", bad, got)
		}
	}
	var length [4]byte
	binary.BigEndian.PutUint32(length[:], 256*1024+1)
	if _, err := client.Write(length[:]); err != nil {
		t.Fatal(err)
	}
	if err := <-done; err == nil {
		t.Fatal("oversized frame was accepted")
	}
	if f.signed != 0 {
		t.Fatal("malformed requests reached signing")
	}
}

func exchange(t *testing.T, conn net.Conn, body []byte) []byte {
	t.Helper()
	frame := make([]byte, 4, 4+len(body))
	binary.BigEndian.PutUint32(frame, uint32(len(body)))
	if _, err := conn.Write(append(frame, body...)); err != nil {
		t.Fatal(err)
	}
	var lenBytes [4]byte
	if _, err := io.ReadFull(conn, lenBytes[:]); err != nil {
		t.Fatal(err)
	}
	size := binary.BigEndian.Uint32(lenBytes[:])
	if size > 256*1024 {
		t.Fatalf("unexpected response length: %d", size)
	}
	res := make([]byte, size)
	if _, err := io.ReadFull(conn, res); err != nil {
		t.Fatal(err)
	}
	return res
}
