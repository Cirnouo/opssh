package engine_test

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/rsa"
	"encoding/pem"
	"errors"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"golang.org/x/crypto/ssh"

	"opssh/internal/engine"
)

type providerFunc func(context.Context, engine.Key) ([]byte, error)

func (p providerFunc) Load(ctx context.Context, key engine.Key) ([]byte, error) {
	return p(ctx, key)
}

func keyFixture(t *testing.T, private any) (engine.Key, func() []byte, ssh.PublicKey) {
	t.Helper()
	signer, err := ssh.NewSignerFromKey(private)
	if err != nil {
		t.Fatal(err)
	}
	public := signer.PublicKey()
	block, err := ssh.MarshalPrivateKey(private, "")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { clear(block.Bytes) })
	return engine.Key{ID: "key", VaultID: "vault", ItemID: "item", Name: "Test key", Public: public.Marshal()},
		func() []byte { return pem.EncodeToMemory(block) }, public
}

func edFixture(t *testing.T) (engine.Key, func() []byte, ssh.PublicKey) {
	t.Helper()
	_, private, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { clear(private) })
	return keyFixture(t, private)
}

func process(pid uint32, created uint64) engine.Process {
	return engine.Process{Instance: engine.Instance{PID: pid, Created: created}, Path: "test.exe"}
}

func peer(chain ...engine.Process) engine.Peer {
	return engine.Peer{
		Chain: chain,
		Alive: func(instance engine.Instance) bool {
			for _, p := range chain {
				if p.Instance == instance {
					return true
				}
			}
			return false
		},
		Connected: func() bool { return true },
	}
}

func makeEngine(t *testing.T, opts engine.Options) *engine.Engine {
	t.Helper()
	e, err := engine.New(opts)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(e.Close)
	return e
}

func accept(*ssh.Signature) error { return nil }

func TestListIsIndependentOfProviderAndCallerMutations(t *testing.T) {
	key, _, _ := edFixture(t)
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key}
	e := makeEngine(t, opts)
	want := bytes.Clone(key.Public)
	key.Public[0] ^= 1
	listed := e.List()
	if len(listed) != 1 || !bytes.Equal(listed[0].Public, want) {
		t.Fatal("catalog lost its indexed public key")
	}
	listed[0].Public[0] ^= 1
	if !bytes.Equal(e.List()[0].Public, want) {
		t.Fatal("caller mutated catalog")
	}
	if e.Status().Loaded[key.ID] {
		t.Fatal("listing loaded private material")
	}
}

func TestSigningRejectsUnsupportedFlagsBeforeApproval(t *testing.T) {
	key, _, _ := edFixture(t)
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key}
	opts.Approve = func(context.Context, engine.Prompt) (engine.Approval, error) {
		t.Error("unsupported request reached approval")
		return engine.Approval{Scope: "once"}, nil
	}
	e := makeEngine(t, opts)
	err := e.Sign(t.Context(), peer(process(1, 1)), key.Public, []byte("sign me"), 2, accept)
	if !errors.Is(err, engine.ErrUnsupportedFlags) {
		t.Fatalf("got %v", err)
	}
	badBlob := append(bytes.Clone(key.Public), 0)
	err = e.Sign(t.Context(), peer(process(1, 1)), badBlob, []byte("sign me"), 0, accept)
	if !errors.Is(err, engine.ErrUnknownKey) {
		t.Fatalf("got %v for an altered blob", err)
	}
}

func TestSignsWithExplicitRSASHA2Flags(t *testing.T) {
	private, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatal(err)
	}
	key, secret, public := keyFixture(t, private)
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key}
	opts.Provider = providerFunc(func(context.Context, engine.Key) ([]byte, error) { return secret(), nil })
	opts.Approve = func(context.Context, engine.Prompt) (engine.Approval, error) {
		return engine.Approval{Scope: "once"}, nil
	}
	e := makeEngine(t, opts)
	p := peer(process(1, 1))
	data := []byte("sign me")
	if err := e.Sign(t.Context(), p, key.Public, data, 0, accept); !errors.Is(err, engine.ErrUnsupportedFlags) {
		t.Fatalf("RSA default flags returned %v", err)
	}
	for _, tt := range []struct {
		flag uint32
		want string
	}{{2, "rsa-sha2-256"}, {4, "rsa-sha2-512"}} {
		published := false
		err := e.Sign(t.Context(), p, key.Public, data, tt.flag, func(signature *ssh.Signature) error {
			published = true
			if signature.Format != tt.want {
				t.Errorf("flag %d published %q, want %q", tt.flag, signature.Format, tt.want)
			}
			return public.Verify(data, signature)
		})
		if err != nil || !published {
			t.Fatalf("flag %d failed to publish a valid signature: %v", tt.flag, err)
		}
	}
}

func TestRejectsEd25519PrivateKeyWithInconsistentSeed(t *testing.T) {
	_, private, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { clear(private) })
	private[0] ^= 1
	key, secret, _ := keyFixture(t, private)
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key}
	var returned []byte
	opts.Provider = providerFunc(func(context.Context, engine.Key) ([]byte, error) {
		returned = secret()
		return returned, nil
	})
	e := makeEngine(t, opts)
	if _, err := e.Preload(t.Context(), key.ID); !errors.Is(err, engine.ErrInvalidKey) {
		t.Fatalf("got %v for an inconsistent seed", err)
	}
	if state := e.Status(); state.Loaded[key.ID] || state.Errors[key.ID] != "invalid-key" {
		t.Fatalf("unexpected status: %+v", state)
	}
	if !bytes.Equal(returned, make([]byte, len(returned))) {
		t.Fatal("provider's transferred buffer was not cleared")
	}
}

func TestHostApprovalRequiresTheLiveOriginalAncestor(t *testing.T) {
	key, secret, public := edFixture(t)
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key}
	opts.Provider = providerFunc(func(context.Context, engine.Key) ([]byte, error) { return secret(), nil })
	var prompts atomic.Int32
	opts.Approve = func(context.Context, engine.Prompt) (engine.Approval, error) {
		if prompts.Add(1) == 1 {
			return engine.Approval{Scope: "app", Candidate: 1}, nil
		}
		return engine.Approval{Scope: "deny"}, nil
	}
	e := makeEngine(t, opts)
	host := process(20, 10)
	data := []byte("host request")
	sign := func(p engine.Peer) error {
		return e.Sign(t.Context(), p, key.Public, data, 0, func(signature *ssh.Signature) error {
			return public.Verify(data, signature)
		})
	}
	client := peer(process(21, 11), host)
	client.ApprovalApp = func(context.Context) (int, bool) { return 1, true }
	if err := sign(client); err != nil {
		t.Fatal(err)
	}
	if err := sign(peer(process(22, 12), host)); err != nil {
		t.Fatalf("fresh descendant could not reuse the grant: %v", err)
	}
	if err := sign(peer(host)); err != nil {
		t.Fatalf("the approved host itself could not reuse the grant: %v", err)
	}
	for _, p := range []engine.Peer{
		peer(process(23, 13), process(30, 2)),
		peer(process(24, 14), process(20, 13)),
		{Chain: []engine.Process{process(25, 15), host}, Alive: func(i engine.Instance) bool { return i.PID == 25 }, Connected: func() bool { return true }},
	} {
		if err := sign(p); !errors.Is(err, engine.ErrDenied) {
			t.Fatalf("unrelated, reused or dead ancestor returned %v", err)
		}
	}
}

func TestFreshFixedApprovalExpiresDuringLoad(t *testing.T) {
	key, secret, _ := edFixture(t)
	start := time.Now()
	var elapsed atomic.Int64
	opts := engine.DefaultOptions()
	opts.Now = func() time.Time { return start.Add(time.Duration(elapsed.Load())) }
	opts.Keys = []engine.Key{key}
	opts.ApprovalCache = engine.Policy{Mode: "fixed", TTL: time.Second}
	opts.Approve = func(context.Context, engine.Prompt) (engine.Approval, error) {
		return engine.Approval{Scope: "app"}, nil
	}
	opts.Provider = providerFunc(func(context.Context, engine.Key) ([]byte, error) {
		elapsed.Store(int64(2 * time.Second))
		return secret(), nil
	})
	e := makeEngine(t, opts)
	err := e.Sign(t.Context(), peer(process(1, 1)), key.Public, []byte("expired"), 0, func(*ssh.Signature) error {
		t.Error("published after the approval deadline")
		return nil
	})
	if !errors.Is(err, engine.ErrStale) {
		t.Fatalf("expired approval returned %v", err)
	}
}

func TestFailedPublicationDoesNotCommitApproval(t *testing.T) {
	for _, scope := range []string{"app", "all"} {
		t.Run(scope, func(t *testing.T) {
			key, secret, _ := edFixture(t)
			opts := engine.DefaultOptions()
			opts.Keys = []engine.Key{key}
			opts.Provider = providerFunc(func(context.Context, engine.Key) ([]byte, error) { return secret(), nil })
			var prompts atomic.Int32
			opts.Approve = func(context.Context, engine.Prompt) (engine.Approval, error) {
				if prompts.Add(1) == 1 {
					return engine.Approval{Scope: scope}, nil
				}
				return engine.Approval{Scope: "deny"}, nil
			}
			e := makeEngine(t, opts)
			p := peer(process(1, 1))
			p.Logon = [32]byte{1}
			data := []byte("sign me")
			err := e.Sign(t.Context(), p, key.Public, data, 0, func(*ssh.Signature) error { return errors.New("connection gone") })
			if !errors.Is(err, engine.ErrPublish) {
				t.Fatalf("got %v", err)
			}
			if err := e.Sign(t.Context(), p, key.Public, data, 0, accept); !errors.Is(err, engine.ErrDenied) {
				t.Fatalf("failed publication seeded an approval: %v", err)
			}
		})
	}
}

func TestCacheExpiryAndInvalidationStayIndependent(t *testing.T) {
	key, secret, _ := edFixture(t)
	var loads, prompts atomic.Int32
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key}
	var clock atomic.Int64
	start := time.Now()
	opts.Now = func() time.Time { return start.Add(time.Duration(clock.Load())) }
	opts.Provider = providerFunc(func(context.Context, engine.Key) ([]byte, error) {
		loads.Add(1)
		return secret(), nil
	})
	opts.Approve = func(context.Context, engine.Prompt) (engine.Approval, error) {
		prompts.Add(1)
		return engine.Approval{Scope: "app"}, nil
	}
	e := makeEngine(t, opts)
	sign := func(wantLoads, wantPrompts int32) {
		t.Helper()
		if err := e.Sign(t.Context(), peer(process(1, 1)), key.Public, []byte("sign me"), 0, accept); err != nil {
			t.Fatal(err)
		}
		if loads.Load() != wantLoads || prompts.Load() != wantPrompts {
			t.Fatalf("got %d loads and %d prompts, want %d and %d", loads.Load(), prompts.Load(), wantLoads, wantPrompts)
		}
	}
	sign(1, 1)
	clock.Add(int64(5 * time.Minute))
	sign(2, 1)
	clock.Add(int64(5 * time.Minute))
	if _, err := e.Preload(t.Context(), key.ID); err != nil {
		t.Fatal(err)
	}
	sign(3, 2)
	e.Revoke(key.ID)
	sign(3, 3)
	e.EvictKeys()
	sign(4, 3)
}

func TestLockRetiresLatePrivateKey(t *testing.T) {
	key, secret, _ := edFixture(t)
	started := make(chan struct{})
	returnKey := make(chan struct{})
	releaseKey := sync.OnceFunc(func() { close(returnKey) })
	defer releaseKey()
	returned := make(chan struct{})
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key}
	opts.Provider = providerFunc(func(context.Context, engine.Key) ([]byte, error) {
		close(started)
		<-returnKey
		defer close(returned)
		return secret(), nil
	})
	e := makeEngine(t, opts)
	result := make(chan error, 1)
	go func() {
		_, err := e.Preload(t.Context(), key.ID)
		result <- err
	}()
	<-started
	if state := e.Status(); !state.Loading[key.ID] || state.Loaded[key.ID] {
		t.Fatalf("pending provider request has the wrong status: %+v", state)
	}
	e.Lock()
	if err := <-result; !errors.Is(err, engine.ErrLocked) {
		t.Fatalf("pending preload returned %v", err)
	}
	e.Unlock()
	releaseKey()
	<-returned
	if state := e.Status(); state.Locked || state.Loading[key.ID] || state.Loaded[key.ID] {
		t.Fatalf("late result became available: %+v", state)
	}
}

func TestRevokeRetiresPendingApproval(t *testing.T) {
	key, secret, _ := edFixture(t)
	started := make(chan struct{})
	returnApproval := make(chan struct{})
	releaseApproval := sync.OnceFunc(func() { close(returnApproval) })
	defer releaseApproval()
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key}
	opts.Provider = providerFunc(func(context.Context, engine.Key) ([]byte, error) { return secret(), nil })
	opts.Approve = func(context.Context, engine.Prompt) (engine.Approval, error) {
		close(started)
		<-returnApproval
		return engine.Approval{Scope: "app"}, nil
	}
	e := makeEngine(t, opts)
	result := make(chan error, 1)
	go func() {
		result <- e.Sign(t.Context(), peer(process(1, 1)), key.Public, []byte("sign me"), 0, func(*ssh.Signature) error {
			t.Error("revoked request published")
			return nil
		})
	}()
	<-started
	e.Revoke(key.ID)
	if err := <-result; !errors.Is(err, engine.ErrStale) {
		t.Fatalf("revoked request returned %v", err)
	}
	releaseApproval()
}

type waitingContext struct {
	context.Context
	waiting chan struct{}
	once    atomic.Bool
}

func (ctx *waitingContext) Done() <-chan struct{} {
	if ctx.once.CompareAndSwap(false, true) {
		close(ctx.waiting)
	}
	return ctx.Context.Done()
}

func TestCacheOffSharesOnlyTheActiveLoad(t *testing.T) {
	key, secret, _ := edFixture(t)
	var loads atomic.Int32
	finishLoad := make(chan struct{})
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key}
	opts.KeyCache = engine.Policy{Mode: "off"}
	opts.Provider = providerFunc(func(ctx context.Context, _ engine.Key) ([]byte, error) {
		loads.Add(1)
		select {
		case <-finishLoad:
			return secret(), nil
		case <-ctx.Done():
			return nil, ctx.Err()
		}
	})
	e := makeEngine(t, opts)
	result := make(chan error, 2)
	for range 2 {
		ctx := &waitingContext{Context: t.Context(), waiting: make(chan struct{})}
		go func() {
			_, err := e.Preload(ctx, key.ID)
			result <- err
		}()
		select {
		case <-ctx.waiting:
		case err := <-result:
			t.Fatalf("preload finished before joining: %v", err)
		case <-time.After(5 * time.Second):
			t.Fatal("preload never joined the provider request")
		}
	}
	close(finishLoad)
	for range 2 {
		if err := <-result; err != nil {
			t.Fatal(err)
		}
	}
	if e.Status().Loaded[key.ID] || loads.Load() != 1 {
		t.Fatalf("concurrent waiters did not share only the active result: %d calls", loads.Load())
	}
	if _, err := e.Preload(t.Context(), key.ID); err != nil || loads.Load() != 2 {
		t.Fatalf("later preload did not fetch fresh material: %d calls, %v", loads.Load(), err)
	}
}

func TestLoadTimesOutWhileProviderStillRunning(t *testing.T) {
	key, _, _ := edFixture(t)
	finish := make(chan struct{})
	defer close(finish)
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key}
	opts.LoadTimeout = 20 * time.Millisecond
	opts.Provider = providerFunc(func(context.Context, engine.Key) ([]byte, error) {
		<-finish
		return nil, errors.New("raw provider detail")
	})
	e := makeEngine(t, opts)
	if _, err := e.Preload(t.Context(), key.ID); !errors.Is(err, engine.ErrLoadTimeout) {
		t.Fatalf("got %v", err)
	}
	if state := e.Status(); state.Loaded[key.ID] || state.Errors[key.ID] != "provider-timeout" {
		t.Fatalf("unexpected status: %+v", state)
	}
}

func TestSignRechecksConnectionAfterLoad(t *testing.T) {
	key, secret, _ := edFixture(t)
	var connected atomic.Bool
	connected.Store(true)
	var prompts atomic.Int32
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key}
	opts.Provider = providerFunc(func(context.Context, engine.Key) ([]byte, error) {
		connected.Store(false)
		return secret(), nil
	})
	opts.Approve = func(context.Context, engine.Prompt) (engine.Approval, error) {
		if prompts.Add(1) == 1 {
			return engine.Approval{Scope: "app"}, nil
		}
		return engine.Approval{Scope: "deny"}, nil
	}
	e := makeEngine(t, opts)
	p := peer(process(1, 1))
	p.Connected = connected.Load
	data := []byte("sign me")
	err := e.Sign(t.Context(), p, key.Public, data, 0, func(*ssh.Signature) error {
		t.Error("disconnected request published")
		return nil
	})
	if !errors.Is(err, engine.ErrPeerGone) {
		t.Fatalf("disconnected request returned %v", err)
	}
	connected.Store(true)
	if err := e.Sign(t.Context(), p, key.Public, data, 0, accept); !errors.Is(err, engine.ErrDenied) {
		t.Fatalf("disconnected request seeded an approval: %v", err)
	}
}

func TestRevocationWaitsForPublication(t *testing.T) {
	key, secret, _ := edFixture(t)
	var prompts atomic.Int32
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key}
	opts.Provider = providerFunc(func(context.Context, engine.Key) ([]byte, error) { return secret(), nil })
	opts.Approve = func(context.Context, engine.Prompt) (engine.Approval, error) {
		if prompts.Add(1) == 1 {
			return engine.Approval{Scope: "app"}, nil
		}
		return engine.Approval{Scope: "deny"}, nil
	}
	e := makeEngine(t, opts)
	started := make(chan struct{})
	finish := make(chan struct{})
	result := make(chan error, 1)
	p := peer(process(1, 1))
	data := []byte("sign me")
	go func() {
		result <- e.Sign(t.Context(), p, key.Public, data, 0, func(*ssh.Signature) error {
			close(started)
			<-finish
			return nil
		})
	}()
	select {
	case <-started:
	case err := <-result:
		close(finish)
		t.Fatalf("request failed before publication: %v", err)
	}
	revoking := make(chan struct{})
	revoked := make(chan struct{})
	go func() {
		close(revoking)
		e.Revoke(key.ID)
		close(revoked)
	}()
	<-revoking
	select {
	case <-revoked:
		t.Error("revocation completed before the active publication")
	case <-time.After(25 * time.Millisecond):
	}
	close(finish)
	if err := <-result; err != nil {
		t.Fatal(err)
	}
	<-revoked
	if err := e.Sign(t.Context(), p, key.Public, data, 0, accept); !errors.Is(err, engine.ErrDenied) {
		t.Fatalf("revoked grant survived: %v", err)
	}
}

func TestIdleApprovalOnlyRenewsOnSuccessfulPublication(t *testing.T) {
	key, secret, _ := edFixture(t)
	var clock atomic.Int64
	start := time.Now()
	var prompts atomic.Int32
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key}
	opts.ApprovalCache = engine.Policy{Mode: "idle", TTL: time.Minute}
	opts.Now = func() time.Time { return start.Add(time.Duration(clock.Load())) }
	opts.Provider = providerFunc(func(context.Context, engine.Key) ([]byte, error) { return secret(), nil })
	opts.Approve = func(context.Context, engine.Prompt) (engine.Approval, error) {
		if prompts.Add(1) == 1 {
			return engine.Approval{Scope: "app"}, nil
		}
		return engine.Approval{Scope: "deny"}, nil
	}
	e := makeEngine(t, opts)
	p := peer(process(1, 1))
	sign := func(publish func(*ssh.Signature) error) error {
		return e.Sign(t.Context(), p, key.Public, []byte("sign me"), 0, publish)
	}
	if err := sign(accept); err != nil {
		t.Fatal(err)
	}
	clock.Add(int64(45 * time.Second))
	if err := sign(accept); err != nil {
		t.Fatal(err)
	}
	clock.Add(int64(45 * time.Second))
	err := sign(func(*ssh.Signature) error { return errors.New("broken pipe") })
	if !errors.Is(err, engine.ErrPublish) {
		t.Fatalf("successful use did not renew the approval: %v", err)
	}
	clock.Add(int64(15 * time.Second))
	if err := sign(accept); !errors.Is(err, engine.ErrDenied) {
		t.Fatalf("failed publication renewed the approval: %v", err)
	}
}
