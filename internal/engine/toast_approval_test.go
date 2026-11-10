package engine_test

import (
	"context"
	"errors"
	"sync/atomic"
	"testing"

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

func TestLegacyAllApprovalIsDeniedForVerifiedRequester(t *testing.T) {
	key, _, _ := edFixture(t)
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key}
	opts.Provider = providerFunc(func(context.Context, engine.Key) ([]byte, error) {
		t.Error("legacy all-apps decision loaded private material")
		return nil, engine.ErrProvider
	})
	opts.Approve = func(context.Context, engine.Prompt) (engine.Approval, error) {
		return engine.Approval{Scope: "all"}, nil
	}
	e := makeEngine(t, opts)
	client := peer(process(31, 10))
	if err := e.Sign(t.Context(), client, key.Public, nil, 0, accept); !errors.Is(err, engine.ErrDenied) {
		t.Fatalf("legacy all-apps decision returned %v, want denied", err)
	}
	if state := e.Status(); state.Grants[key.ID] != 0 || state.Loaded[key.ID] {
		t.Fatalf("legacy all-apps decision changed transient state: %+v", state)
	}
}

func TestAppApprovalRequiresEnabledCache(t *testing.T) {
	key, _, _ := edFixture(t)
	opts := engine.DefaultOptions()
	opts.Keys = []engine.Key{key}
	opts.ApprovalCache = engine.Policy{Mode: "off"}
	opts.Provider = providerFunc(func(context.Context, engine.Key) ([]byte, error) {
		t.Error("app approval with cache off loaded private material")
		return nil, engine.ErrProvider
	})
	opts.Approve = func(_ context.Context, prompt engine.Prompt) (engine.Approval, error) {
		if prompt.CacheAllowed {
			t.Error("prompt did not reflect the disabled approval cache")
		}
		return engine.Approval{Scope: "app"}, nil
	}
	e := makeEngine(t, opts)
	if err := e.Sign(t.Context(), peer(process(31, 10)), key.Public, nil, 0, accept); !errors.Is(err, engine.ErrDenied) {
		t.Fatalf("app approval with cache off returned %v", err)
	}
}
