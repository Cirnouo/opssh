package desktop

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"errors"
	"testing"
	"time"

	"golang.org/x/crypto/ssh"
)

const vaultID = "aaaaaaaaaaaaaaaaaaaaaaaaaa"
const itemID = "bbbbbbbbbbbbbbbbbbbbbbbbbb"

type fakeResolve func(context.Context, string) (string, error)

func (f fakeResolve) Resolve(ctx context.Context, ref string) (string, error) { return f(ctx, ref) }

func TestPublicRefreshOnlyResolvesPublicField(t *testing.T) {
	pk, _, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	pub, err := ssh.NewPublicKey(pk)
	if err != nil {
		t.Fatal(err)
	}
	var references []string
	b := newBackend("Test", func(context.Context, string) (resolver, error) {
		return fakeResolve(func(_ context.Context, ref string) (string, error) {
			references = append(references, ref)
			return string(ssh.MarshalAuthorizedKey(pub)), nil
		}), nil
	})
	got, err := b.Public(context.Background(), vaultID, itemID)
	if err != nil || string(got) != string(pub.Marshal()) {
		t.Fatalf("public lookup: %v", err)
	}
	if len(references) != 1 || references[0] != "op://"+vaultID+"/"+itemID+"/public key" {
		t.Fatalf("wrong field requested: %q", references)
	}
}

func TestTimedOutNativeCallRemainsBounded(t *testing.T) {
	started, release := make(chan struct{}), make(chan struct{})
	defer close(release)
	b := newBackend("Test", func(context.Context, string) (resolver, error) {
		return fakeResolve(func(context.Context, string) (string, error) {
			close(started)
			<-release
			return "late-test-value", nil
		}), nil
	})
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	done := make(chan error, 1)
	go func() { _, err := b.Private(ctx, vaultID, itemID); done <- err }()
	select {
	case <-started:
	case <-time.After(time.Second):
		t.Fatal("fake backend did not begin")
	}
	cancel()
	select {
	case err := <-done:
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("unexpected failure: %v", err)
		}
	case <-time.After(time.Second):
		t.Fatal("request did not cancel")
	}
	if _, err := b.Private(context.Background(), vaultID, itemID); !errors.Is(err, ErrBusy) {
		t.Fatalf("blocked SDK started extra work: %v", err)
	}
}
