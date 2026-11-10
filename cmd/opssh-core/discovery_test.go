package main

import (
	"context"
	"crypto/ed25519"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"golang.org/x/crypto/ssh"

	"opssh/internal/config"
	"opssh/internal/control"
	"opssh/internal/desktop"
)

type pausedDiscovery struct {
	offlineVault
	started chan struct{}
	release chan struct{}
}

func (v *pausedDiscovery) Discover(ctx context.Context, scope config.Scope) ([]desktop.Vault, error) {
	return v.DiscoverWithProgress(ctx, scope, func(int, int) {})
}

func (v *pausedDiscovery) DiscoverWithProgress(_ context.Context, _ config.Scope, report func(int, int)) ([]desktop.Vault, error) {
	report(1, 2)
	close(v.started)
	<-v.release
	// A synchronous provider can return success after cancellation.
	return []desktop.Vault{{ID: strings.Repeat("c", 26), Name: "Late vault"}}, nil
}

func TestDiscoveryCancellationIsBoundAndRestoresOnlyItsOwnIndex(t *testing.T) {
	for _, replaceScope := range []bool{false, true} {
		name := "same scope"
		if replaceScope {
			name = "changed scope"
		}
		t.Run(name, func(t *testing.T) {
			key, err := ssh.NewPublicKey(ed25519.PublicKey(make([]byte, ed25519.PublicKeySize)))
			if err != nil {
				t.Fatal(err)
			}
			path := filepath.Join(t.TempDir(), "settings.json")
			cfg := config.Default()
			cfg.Account = "Fixture"
			cfg.Index = config.IndexReady
			cfg.Keys = []config.Key{{VaultID: strings.Repeat("a", 26), ItemID: strings.Repeat("b", 26), Public: key.Marshal()}}
			if err := config.Save(path, cfg); err != nil {
				t.Fatal(err)
			}
			store := &pausedDiscovery{started: make(chan struct{}), release: make(chan struct{})}
			app, err := newApplication(path, "fixture", cfg, nil, func(string) vault { return store })
			if err != nil {
				t.Fatal(err)
			}
			defer app.engine.Close()
			ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
			defer cancel()
			release := sync.OnceFunc(func() { close(store.release) })
			defer release()
			id := strings.Repeat("a", 32)
			body := map[string]string{"operationId": id}
			done := make(chan control.Result, 1)
			go func() { done <- app.command(ctx, control.Event("discover", "start", body)) }()
			select {
			case <-store.started:
			case <-ctx.Done():
				t.Fatal("discovery did not start")
			}
			saved, err := config.Read(path)
			if err != nil || len(saved.Keys) != 0 || len(app.engine.List()) != 0 {
				t.Fatalf("active scan must withdraw the prior index: %+v, %v", saved, err)
			}
			result, handled := app.priority(control.Event("discovery-progress", "progress", body))
			if !handled || !result.OK || result.Data != (discoveryProgress{Phase: "vaults", Completed: 1, Total: 2}) {
				t.Fatalf("wrong progress: %+v", result)
			}
			result, handled = app.priority(control.Event("cancel-discovery", "cancel", map[string]string{"operationId": strings.Repeat("b", 32)}))
			if !handled || result.OK || result.ErrorCode != "discovery-not-active" {
				t.Fatalf("unrelated cancellation was accepted: %+v", result)
			}
			if replaceScope {
				saved.Scope.Default = "exclude"
				if err := config.Save(path, saved); err != nil {
					t.Fatal(err)
				}
			}
			result, handled = app.priority(control.Event("cancel-discovery", "cancel", body))
			if !handled || !result.OK {
				t.Fatalf("matching cancellation failed: %+v", result)
			}
			release()
			select {
			case result = <-done:
			case <-ctx.Done():
				t.Fatal("discovery cancellation did not complete")
			}
			saved, err = config.Read(path)
			if err != nil {
				t.Fatal(err)
			}
			if replaceScope {
				if result.ErrorCode == "discovery-canceled" || saved.Scope.Default != "exclude" ||
					len(saved.Keys) != 0 || len(app.engine.List()) != 0 {
					t.Fatalf("cancellation revived a stale index: %+v, %+v", result, saved)
				}
			} else if result.ErrorCode != "discovery-canceled" || saved.Index != config.IndexReady ||
				len(saved.Keys) != 1 || len(app.engine.List()) != 1 {
				t.Fatalf("cancellation failed to restore the prior index: %+v, %+v", result, saved)
			}
		})
	}
}
