package engine_test

import (
	"context"
	"errors"
	"sync/atomic"
	"testing"
	"time"

	"opssh/internal/engine"
)

func TestUnavailableApprovalDoesNotLoadOrGrant(t *testing.T) {
	key, _, _ := edFixture(t)
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key}
	opts.Provider = providerFunc(func(context.Context, engine.Key) ([]byte, error) {
		t.Error("unavailable approval loaded private material")
		return nil, engine.ErrProvider
	})
	opts.Approve = func(context.Context, engine.Prompt) (engine.Approval, error) {
		return engine.Approval{Scope: "unavailable"}, nil
	}
	e := makeEngine(t, opts)
	err := e.Sign(t.Context(), peer(process(31, 10)), key.Public, nil, 0, accept)
	if !errors.Is(err, engine.ErrUnavailable) {
		t.Fatalf("unavailable approval must have its own outcome, got %v", err)
	}
	if state := e.Status(); state.Grants[key.ID] != 0 || state.Loaded[key.ID] {
		t.Fatalf("unavailable approval changed transient state: %+v", state)
	}
}

func TestAppApprovalRejectsADifferentCandidate(t *testing.T) {
	key, _, _ := edFixture(t)
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key}
	opts.Provider = providerFunc(func(context.Context, engine.Key) ([]byte, error) {
		t.Error("mismatched approval loaded private material")
		return nil, engine.ErrProvider
	})
	opts.Approve = func(_ context.Context, prompt engine.Prompt) (engine.Approval, error) {
		if prompt.AppCandidate != 1 {
			t.Errorf("prompt selected index %d, want 1", prompt.AppCandidate)
		}
		return engine.Approval{Scope: "app", Candidate: 2}, nil
	}
	e := makeEngine(t, opts)
	client := peer(process(31, 30), process(29, 20), process(25, 10))
	client.ApprovalApp = func(context.Context) (int, bool) { return 1, true }
	if err := e.Sign(t.Context(), client, key.Public, nil, 0, accept); !errors.Is(err, engine.ErrDenied) {
		t.Fatalf("app approval for a different candidate returned %v", err)
	}
}

func TestRequesterAppApprovalRequiresAWindowToCoverDescendants(t *testing.T) {
	for _, test := range []struct {
		name       string
		ownsWindow bool
		want       error
	}{
		{name: "eligible window owner", ownsWindow: true},
		{name: "requester fallback", want: engine.ErrDenied},
	} {
		t.Run(test.name, func(t *testing.T) {
			key, secret, _ := edFixture(t)
			opts := engine.DefaultOptions()
			opts.Keys = []engine.Key{key}
			opts.Provider = providerFunc(func(context.Context, engine.Key) ([]byte, error) { return secret(), nil })
			var prompts atomic.Int32
			opts.Approve = func(context.Context, engine.Prompt) (engine.Approval, error) {
				if prompts.Add(1) == 1 {
					return engine.Approval{Scope: "app", Candidate: 0}, nil
				}
				return engine.Approval{Scope: "deny"}, nil
			}
			e := makeEngine(t, opts)
			requester := process(31, 10)
			client := peer(requester)
			client.ApprovalApp = func(context.Context) (int, bool) { return 0, test.ownsWindow }
			for range 2 {
				if err := e.Sign(t.Context(), client, key.Public, nil, 0, accept); err != nil {
					t.Fatalf("app approval for the verified requester returned %v", err)
				}
			}
			descendant := peer(process(32, 11), requester)
			if err := e.Sign(t.Context(), descendant, key.Public, nil, 0, accept); !errors.Is(err, test.want) {
				t.Fatalf("descendant sign returned %v, want %v", err, test.want)
			}
		})
	}
}

func TestAllApprovalIsLimitedToTheExactKeyAndVerifiedLogon(t *testing.T) {
	key, secret, _ := edFixture(t)
	otherKey, otherSecret, _ := edFixture(t)
	otherKey.ID = "other"
	start := time.Now()
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key, otherKey}
	opts.Now = func() time.Time { return start }
	opts.Provider = providerFunc(func(_ context.Context, k engine.Key) ([]byte, error) {
		if k.ID == key.ID {
			return secret(), nil
		}
		return otherSecret(), nil
	})
	var prompts atomic.Int32
	opts.Approve = func(context.Context, engine.Prompt) (engine.Approval, error) {
		if prompts.Add(1) == 1 {
			return engine.Approval{Scope: "all"}, nil
		}
		return engine.Approval{Scope: "deny"}, nil
	}
	e := makeEngine(t, opts)
	requester, sameLogon, differentLogon := peer(process(31, 10)), peer(process(32, 11)), peer(process(33, 12))
	requester.Logon, sameLogon.Logon, differentLogon.Logon = [32]byte{1}, [32]byte{1}, [32]byte{2}
	requester.Retain = func(engine.Instance) (func() bool, func(), bool) {
		t.Error("same-logon grant tried to retain its requesting process")
		return nil, nil, false
	}
	sign := func(p engine.Peer, k engine.Key) error {
		return e.Sign(t.Context(), p, k.Public, nil, 0, accept)
	}
	if err := sign(requester, key); err != nil {
		t.Fatal(err)
	}
	if err := sign(sameLogon, key); err != nil {
		t.Fatalf("same-logon process could not reuse approval: %v", err)
	}
	if err := sign(differentLogon, key); !errors.Is(err, engine.ErrDenied) {
		t.Fatalf("same-logon approval leaked to a different logon: %v", err)
	}
	if err := sign(sameLogon, otherKey); !errors.Is(err, engine.ErrDenied) {
		t.Fatalf("same-logon approval leaked to a different key: %v", err)
	}
	grants, err := e.Grants(key.ID)
	if err != nil || len(grants) != 1 || grants[0].ID == "" || grants[0].Scope != "all" ||
		grants[0].PID != 0 || grants[0].Name != "" || !grants[0].ExpiresAt.Equal(start.Add(10*time.Minute)) {
		t.Fatalf("unexpected same-logon grant display: %+v (%v)", grants, err)
	}
	if err := e.RevokeGrant(key.ID, grants[0].ID); err != nil {
		t.Fatal(err)
	}
	if err := sign(sameLogon, key); !errors.Is(err, engine.ErrDenied) {
		t.Fatalf("revoked same-logon approval remained usable: %v", err)
	}
}

func TestReusableApprovalRequiresCacheAndVerifiedScope(t *testing.T) {
	for _, test := range []struct {
		name  string
		scope string
		off   bool
		logon [32]byte
	}{
		{name: "app with cache off", scope: "app", off: true, logon: [32]byte{1}},
		{name: "all with cache off", scope: "all", off: true, logon: [32]byte{1}},
		{name: "all without verified logon", scope: "all"},
	} {
		t.Run(test.name, func(t *testing.T) {
			key, _, _ := edFixture(t)
			opts := engine.DefaultOptions()
			opts.Keys = []engine.Key{key}
			if test.off {
				opts.ApprovalCache = engine.Policy{Mode: "off"}
			}
			opts.Provider = providerFunc(func(context.Context, engine.Key) ([]byte, error) {
				t.Error("invalid reusable approval loaded private material")
				return nil, engine.ErrProvider
			})
			opts.Approve = func(_ context.Context, prompt engine.Prompt) (engine.Approval, error) {
				if prompt.CacheAllowed == test.off {
					t.Error("prompt did not reflect the approval cache policy")
				}
				return engine.Approval{Scope: test.scope}, nil
			}
			e := makeEngine(t, opts)
			client := peer(process(31, 10))
			client.Logon = test.logon
			if err := e.Sign(t.Context(), client, key.Public, nil, 0, accept); !errors.Is(err, engine.ErrDenied) {
				t.Fatalf("invalid reusable approval returned %v", err)
			}
		})
	}
}
