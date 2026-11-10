package engine_test

import (
	"context"
	"errors"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"golang.org/x/crypto/ssh"

	"opssh/internal/engine"
)

func TestPreloadAllSkipsAKeyEvictedDuringAnEarlierLoad(t *testing.T) {
	first, firstSecret, _ := edFixture(t)
	second, secondSecret, _ := edFixture(t)
	second.ID = "second"
	started := make(chan struct{})
	wait := make(chan struct{})
	release := sync.OnceFunc(func() { close(wait) })
	defer release()

	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{first, second}
	opts.Provider = providerFunc(func(ctx context.Context, key engine.Key) ([]byte, error) {
		if key.ID == first.ID {
			close(started)
			select {
			case <-wait:
			case <-ctx.Done():
				return nil, ctx.Err()
			}
			return firstSecret(), nil
		}
		return secondSecret(), nil
	})
	e := makeEngine(t, opts)
	type batchResult struct {
		report engine.PreloadReport
		err    error
	}
	result := make(chan batchResult, 1)
	go func() {
		report, err := e.PreloadAll(t.Context())
		result <- batchResult{report, err}
	}()
	<-started
	if err := e.EvictKey(second.ID); err != nil {
		t.Fatal(err)
	}
	release()
	if got := <-result; !errors.Is(got.err, engine.ErrStale) || got.report != (engine.PreloadReport{Selected: 2, Loaded: 1, Failed: 1}) {
		t.Fatalf("preload with an evicted key returned %+v", got)
	}
	if state := e.Status(); !state.Loaded[first.ID] || state.Loaded[second.ID] {
		t.Fatalf("preload used a stale snapshot: %+v", state)
	}
}

func TestPreloadAllReportsUnattemptedKeysAfterCancellation(t *testing.T) {
	first, secret, _ := edFixture(t)
	second, _, _ := edFixture(t)
	second.ID = "second"
	third, _, _ := edFixture(t)
	third.ID = "third"
	started := make(chan struct{})
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{first, second, third}
	opts.Provider = providerFunc(func(ctx context.Context, key engine.Key) ([]byte, error) {
		if key.ID != first.ID {
			t.Error("preload read a later key after cancellation")
			return nil, engine.ErrProvider
		}
		close(started)
		<-ctx.Done()
		return secret(), nil
	})
	e := makeEngine(t, opts)
	ctx, cancel := context.WithCancel(t.Context())
	defer cancel()
	go func() {
		<-started
		cancel()
	}()
	report, err := e.PreloadAll(ctx)
	if !errors.Is(err, context.Canceled) || report != (engine.PreloadReport{Selected: 3, Failed: 1, Skipped: 2, Interrupted: true}) {
		t.Fatalf("canceled preload returned %+v and %v", report, err)
	}
	state := e.Status()
	if state.Loaded[first.ID] || state.Loading[first.ID] || state.Errors[first.ID] != "canceled" || len(state.Errors) != 1 {
		t.Fatalf("cancellation changed unattempted keys or published a result: %+v", state)
	}
}

type gatedDoneContext struct {
	context.Context
	waiting chan struct{}
	proceed chan struct{}
}

func (ctx gatedDoneContext) Done() <-chan struct{} {
	close(ctx.waiting)
	<-ctx.proceed
	return ctx.Context.Done()
}

func TestPreloadDiscardsMaterialWhenCancellationPrecedesProviderCompletion(t *testing.T) {
	key, secret, _ := edFixture(t)
	returnKey := make(chan struct{})
	releaseProvider := sync.OnceFunc(func() { close(returnKey) })
	defer releaseProvider()
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key}
	opts.Provider = providerFunc(func(context.Context, engine.Key) ([]byte, error) {
		<-returnKey
		return secret(), nil
	})
	e := makeEngine(t, opts)
	ctx, cancel := context.WithCancel(t.Context())
	defer cancel()
	waiter := gatedDoneContext{Context: ctx, waiting: make(chan struct{}), proceed: make(chan struct{})}
	releaseWaiter := sync.OnceFunc(func() { close(waiter.proceed) })
	defer releaseWaiter()
	result := make(chan error, 1)
	go func() {
		_, err := e.Preload(waiter, key.ID)
		result <- err
	}()
	<-waiter.waiting
	// Make the cancellation observable while holding the waiter outside its select.
	// The provider must finalize the flight before the waiter can release it.
	cancel()
	releaseProvider()
	tick := time.NewTicker(time.Millisecond)
	defer tick.Stop()
	deadline := time.NewTimer(5 * time.Second)
	defer deadline.Stop()
	for e.Status().Loading[key.ID] {
		select {
		case <-tick.C:
		case <-deadline.C:
			t.Fatal("provider did not finish the load")
		}
	}
	releaseWaiter()
	if err := <-result; !errors.Is(err, context.Canceled) {
		t.Fatalf("canceled waiter returned %v", err)
	}
	if state := e.Status(); state.Loaded[key.ID] || state.Errors[key.ID] != "canceled" {
		t.Fatalf("abandoned load published material or lost cancellation: %+v", state)
	}
}

func TestSelectiveGrantRevocationMatchesOnlyItsIssuance(t *testing.T) {
	key, secret, _ := edFixture(t)
	other, otherSecret, _ := edFixture(t)
	other.ID = "other"
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key, other}
	opts.ApprovalCache = engine.Policy{Mode: "until-exit"}
	opts.KeyCache = engine.Policy{Mode: "until-exit"}
	opts.Provider = providerFunc(func(_ context.Context, k engine.Key) ([]byte, error) {
		if k.ID == key.ID {
			return secret(), nil
		}
		return otherSecret(), nil
	})
	var prompts atomic.Int32
	opts.Approve = func(context.Context, engine.Prompt) (engine.Approval, error) {
		prompts.Add(1)
		return engine.Approval{Scope: "app"}, nil
	}
	e := makeEngine(t, opts)
	client := process(21, 10)
	client.Path = `C:\a\private\client.exe`
	sign := func(k engine.Key, p engine.Process) {
		t.Helper()
		if err := e.Sign(t.Context(), peer(p), k.Public, nil, 0, accept); err != nil {
			t.Fatal(err)
		}
	}
	sign(key, client)
	sign(key, process(22, 11))
	sign(other, client)
	grants, err := e.Grants(key.ID)
	if err != nil || len(grants) != 2 || e.Status().Grants[key.ID] != 2 {
		t.Fatalf("two active grants expected, got %+v (%v)", grants, err)
	}
	var revoked engine.Grant
	for _, grant := range grants {
		if grant.PID == client.PID {
			revoked = grant
		}
	}
	if revoked.ID == "" || revoked.Name != "client.exe" || revoked.Scope != "app" || !revoked.ExpiresAt.IsZero() {
		t.Fatalf("unexpected grant display: %+v", revoked)
	}
	if err := e.RevokeGrant(other.ID, revoked.ID); !errors.Is(err, engine.ErrUnknownGrant) {
		t.Fatalf("cross-key revocation returned %v", err)
	}
	if err := e.RevokeGrant(key.ID, revoked.ID); err != nil {
		t.Fatal(err)
	}
	if state := e.Status(); state.Grants[key.ID] != 1 || state.Grants[other.ID] != 1 || !state.Loaded[key.ID] {
		t.Fatalf("single grant revocation changed unrelated state: %+v", state)
	}
	sign(key, process(22, 11))
	sign(other, client)
	if got := prompts.Load(); got != 3 {
		t.Fatalf("revoking one grant invalidated another, got %d prompts", got)
	}
	sign(key, client)
	if err := e.RevokeGrant(key.ID, revoked.ID); !errors.Is(err, engine.ErrUnknownGrant) {
		t.Fatalf("previous issuance could revoke its replacement: %v", err)
	}
	sign(key, client)
	if got := prompts.Load(); got != 4 {
		t.Fatalf("client did not retain its replacement grant, got %d prompts", got)
	}
	if err := e.EvictKey(key.ID); err != nil {
		t.Fatal(err)
	}
	if state := e.Status(); state.Loaded[key.ID] || !state.Loaded[other.ID] || state.Grants[key.ID] != 2 {
		t.Fatalf("selective eviction changed unrelated state: %+v", state)
	}
}

func TestRevokingGrantCancelsPendingSignatureWithThatGrant(t *testing.T) {
	key, secret, _ := edFixture(t)
	started := make(chan struct{})
	canceled := make(chan struct{})
	var loads atomic.Int32
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key}
	opts.Provider = providerFunc(func(ctx context.Context, _ engine.Key) ([]byte, error) {
		if loads.Add(1) == 1 {
			return secret(), nil
		}
		close(started)
		<-ctx.Done()
		close(canceled)
		return nil, ctx.Err()
	})
	opts.Approve = func(context.Context, engine.Prompt) (engine.Approval, error) {
		return engine.Approval{Scope: "app"}, nil
	}
	e := makeEngine(t, opts)
	client := peer(process(42, 17))
	if err := e.Sign(t.Context(), client, key.Public, nil, 0, accept); err != nil {
		t.Fatal(err)
	}
	grants, err := e.Grants(key.ID)
	if err != nil || len(grants) != 1 {
		t.Fatalf("expected one grant, got %+v (%v)", grants, err)
	}
	if err := e.EvictKey(key.ID); err != nil {
		t.Fatal(err)
	}

	ctx, cancel := context.WithTimeout(t.Context(), 3*time.Second)
	defer cancel()
	result := make(chan error, 1)
	go func() {
		result <- e.Sign(ctx, client, key.Public, nil, 0, func(*ssh.Signature) error {
			t.Error("revoked grant published a signature")
			return nil
		})
	}()
	select {
	case <-started:
	case <-ctx.Done():
		t.Fatal("cached signature did not reach the provider")
	}
	if err := e.RevokeGrant(key.ID, grants[0].ID); err != nil {
		t.Fatal(err)
	}
	if err := <-result; !errors.Is(err, context.Canceled) && !errors.Is(err, engine.ErrStale) {
		t.Fatalf("revoked sign returned %v", err)
	}
	select {
	case <-canceled:
	case <-ctx.Done():
		t.Fatal("revocation left the signer's provider call running")
	}
}

func TestGrantListingRetiresExitedProcessAndExpiredApproval(t *testing.T) {
	key, secret, _ := edFixture(t)
	start := time.Now()
	var elapsed atomic.Int64
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key}
	opts.Now = func() time.Time { return start.Add(time.Duration(elapsed.Load())) }
	opts.ApprovalCache = engine.Policy{Mode: "fixed", TTL: time.Minute}
	opts.Provider = providerFunc(func(context.Context, engine.Key) ([]byte, error) { return secret(), nil })
	opts.Approve = func(context.Context, engine.Prompt) (engine.Approval, error) {
		return engine.Approval{Scope: "app"}, nil
	}
	e := makeEngine(t, opts)

	var alive atomic.Bool
	alive.Store(true)
	var closed atomic.Int32
	requester := peer(process(42, 17))
	requester.Retain = func(instance engine.Instance) (func() bool, func(), bool) {
		if instance != requester.Chain[0].Instance {
			return nil, nil, false
		}
		return alive.Load, func() { closed.Add(1) }, true
	}
	if err := e.Sign(t.Context(), requester, key.Public, nil, 0, accept); err != nil {
		t.Fatal(err)
	}
	grants, err := e.Grants(key.ID)
	if err != nil || len(grants) != 1 || !grants[0].ExpiresAt.Equal(start.Add(time.Minute)) {
		t.Fatalf("fixed expiry missing from the grant: %+v (%v)", grants, err)
	}
	alive.Store(false)
	if state := e.Status(); state.Grants[key.ID] != 0 || closed.Load() != 1 || !state.Loaded[key.ID] {
		t.Fatalf("exited grant was not released independently: %+v", state)
	}
	if err := e.Sign(t.Context(), peer(process(42, 18)), key.Public, nil, 0, accept); err != nil {
		t.Fatal(err)
	}
	elapsed.Store(int64(time.Minute))
	grants, err = e.Grants(key.ID)
	if err != nil || len(grants) != 0 || e.Status().Grants[key.ID] != 0 {
		t.Fatalf("expired grant stayed active: %+v (%v)", grants, err)
	}
}

func TestIndexSuspensionRetainsStateOnlyForUnchangedIdentity(t *testing.T) {
	key, secret, _ := edFixture(t)
	rotated, _, _ := edFixture(t)
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key}
	opts.Provider = providerFunc(func(context.Context, engine.Key) ([]byte, error) { return secret(), nil })
	var prompts atomic.Int32
	opts.Approve = func(context.Context, engine.Prompt) (engine.Approval, error) {
		prompts.Add(1)
		return engine.Approval{Scope: "app"}, nil
	}
	e := makeEngine(t, opts)
	client := peer(process(42, 17))
	sign := func() error { return e.Sign(t.Context(), client, key.Public, nil, 0, accept) }
	if err := sign(); err != nil {
		t.Fatal(err)
	}
	e.SuspendIndex()
	if len(e.List()) != 0 {
		t.Fatal("suspended index continued to advertise keys")
	}
	if err := sign(); !errors.Is(err, engine.ErrStale) {
		t.Fatalf("signing during discovery returned %v", err)
	}
	if _, err := e.Preload(t.Context(), key.ID); !errors.Is(err, engine.ErrStale) {
		t.Fatalf("preload during discovery returned %v", err)
	}
	key.Name = "Renamed"
	if err := e.ReplaceKeys([]engine.Key{key}); err != nil {
		t.Fatal(err)
	}
	e.ResumeIndex()
	if keys := e.List(); len(keys) != 1 || keys[0].Name != key.Name {
		t.Fatalf("resumed index did not publish the rename: %+v", keys)
	}
	if state := e.Status(); !state.Loaded[key.ID] || state.Grants[key.ID] != 1 || state.Locked {
		t.Fatalf("rename lost an unchanged identity's state: %+v", state)
	}
	if err := sign(); err != nil || prompts.Load() != 1 {
		t.Fatalf("rename retired a usable grant: %v", err)
	}
	e.SuspendIndex()
	key.Public = rotated.Public
	if err := e.ReplaceKeys([]engine.Key{key}); err != nil {
		t.Fatal(err)
	}
	e.ResumeIndex()
	if state := e.Status(); state.Loaded[key.ID] || state.Grants[key.ID] != 0 {
		t.Fatalf("rotation kept state for the old public key: %+v", state)
	}
}

func TestSignObservationIdentifiesTheSelectedHost(t *testing.T) {
	key, secret, _ := edFixture(t)
	host := process(24, 11)
	client := peer(process(25, 12), host)
	client.ApprovalApp = func(context.Context) (int, bool) { return 1, true }
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key}
	opts.Provider = providerFunc(func(context.Context, engine.Key) ([]byte, error) { return secret(), nil })
	opts.Approve = func(context.Context, engine.Prompt) (engine.Approval, error) {
		return engine.Approval{Scope: "app", Candidate: 1}, nil
	}
	e := makeEngine(t, opts)
	for range 2 {
		var observations []engine.SignObservation
		err := e.SignObserved(t.Context(), client, key.Public, nil, 0, accept, func(event engine.SignObservation) {
			observations = append(observations, event)
		})
		if err != nil || len(observations) != 1 || observations[0].KeyName != key.Name || observations[0].Anchor != host {
			t.Fatalf("incorrect selected host: %+v (%v)", observations, err)
		}
	}
}
