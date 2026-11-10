package main

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"encoding/json"
	"encoding/pem"
	"errors"
	"maps"
	"path/filepath"
	"strings"
	"testing"

	"golang.org/x/crypto/ssh"

	"opssh/internal/config"
	"opssh/internal/control"
	"opssh/internal/desktop"
)

type preloadVault struct {
	discoveryVault
	load func(context.Context, string, string) ([]byte, error)
}

func (v *preloadVault) Private(ctx context.Context, vaultID, itemID string) ([]byte, error) {
	return v.load(ctx, vaultID, itemID)
}

func TestPreloadCommandsRetainPartialSuccessAndReportSafeResults(t *testing.T) {
	const vaultID = "aaaaaaaaaaaaaaaaaaaaaaaaaa"
	ids := []string{"bbbbbbbbbbbbbbbbbbbbbbbbbb", "cccccccccccccccccccccccccc", "dddddddddddddddddddddddddd"}
	blobs := make(map[string][]byte)
	secrets := make(map[string][]byte)
	defer func() {
		for _, secret := range secrets {
			clear(secret)
		}
	}()
	items := make([]desktop.Item, 0, len(ids))
	for _, id := range ids {
		pk, sk, err := ed25519.GenerateKey(rand.Reader)
		if err != nil {
			t.Fatal(err)
		}
		pub, err := ssh.NewPublicKey(pk)
		if err != nil {
			t.Fatal(err)
		}
		block, err := ssh.MarshalPrivateKey(sk, "")
		clear(sk)
		if err != nil {
			t.Fatal(err)
		}
		blobs[id], secrets[id] = pub.Marshal(), pem.EncodeToMemory(block)
		clear(block.Bytes)
		items = append(items, desktop.Item{ID: id, Name: "Fixture key"})
	}
	calls := make(map[string]int)
	var cancelBatch context.CancelFunc
	const opaqueError = "synthetic provider details"
	store := &preloadVault{
		discoveryVault: discoveryVault{
			vaults: []desktop.Vault{{ID: vaultID, Items: items}},
			resolve: func(_, id string) ([]byte, error) {
				return blobs[id], nil
			},
		},
		load: func(_ context.Context, _, id string) ([]byte, error) {
			calls[id]++
			switch id {
			case ids[1]:
				if cancelBatch != nil {
					cancelBatch()
				}
				return nil, desktop.ErrSessionExpired
			case ids[2]:
				return nil, errors.New(opaqueError)
			default:
				return append([]byte(nil), secrets[id]...), nil
			}
		},
	}
	cfg := config.Default()
	cfg.Account = "Fixture"
	app, err := newApplication(filepath.Join(t.TempDir(), "settings.json"), "fixture", cfg, nil, func(string) vault { return store })
	if err != nil {
		t.Fatal(err)
	}
	defer app.engine.Close()
	result := app.command(t.Context(), control.Event("refresh-index", "index", nil))
	if !result.OK || len(app.engine.List()) != len(ids) || len(calls) != 0 {
		t.Fatal("public discovery failed or read private material")
	}
	inspect := func(result control.Result, selected, loaded, alreadyLoaded, failed, skipped int, code string) {
		t.Helper()
		body, err := json.Marshal(result)
		if err != nil || strings.Contains(string(body), opaqueError) {
			t.Fatal("private result exposed unsanitized provider data")
		}
		var wire struct {
			Data map[string]int `json:"data"`
		}
		want := map[string]int{"selected": selected, "loaded": loaded, "alreadyLoaded": alreadyLoaded, "failed": failed, "skipped": skipped}
		if json.Unmarshal(body, &wire) != nil || !maps.Equal(wire.Data, want) || result.OK != (code == "") || result.ErrorCode != code {
			t.Errorf("unexpected preload result: ok=%v, code=%q, data=%v", result.OK, result.ErrorCode, result.Data)
		}
	}
	result = app.command(t.Context(), control.Event("preload-all", "batch", nil))
	inspect(result, 3, 1, 0, 2, 0, "preload-partial")
	state := result.State.(appState)
	if len(state.Keys) != 3 || !state.Keys[0].Loaded || state.Keys[1].Error != "desktop-session-expired" ||
		state.Keys[2].Error != "private-lookup" {
		t.Error("batch did not retain the imported key and expose independent failure stages")
	}
	for _, key := range state.Keys {
		if key.Grants != 0 {
			t.Error("preloading installed a process approval")
		}
	}
	result = app.command(t.Context(), control.Event("preload", "single", map[string]string{"keyId": vaultID + "/" + ids[0]}))
	inspect(result, 1, 0, 1, 0, 0, "")
	if calls[ids[0]] != 1 {
		t.Error("preloading a loaded key read its private field again")
	}
	result = app.command(t.Context(), control.Event("preload", "expired", map[string]string{"keyId": vaultID + "/" + ids[1]}))
	inspect(result, 1, 0, 0, 1, 0, "desktop-session-expired")
	batchCtx, cancel := context.WithCancel(t.Context())
	defer cancel()
	cancelBatch = cancel
	result = app.command(batchCtx, control.Event("preload-all", "canceled", nil))
	inspect(result, 3, 0, 1, 1, 1, "canceled")
	app.engine.SuspendIndex()
	result = app.command(t.Context(), control.Event("preload-all", "stale", nil))
	inspect(result, 0, 0, 0, 0, 0, "stale")
	app.engine.ResumeIndex()
	next := app.copySettings()
	next.KeyCache = config.Policy{Mode: "off"}
	if err := app.applySettings(next); err != nil {
		t.Fatal(err)
	}
	result = app.command(t.Context(), control.Event("preload-all", "cache-off", nil))
	inspect(result, 0, 0, 0, 0, 0, "cache-off")
	if calls[ids[0]] != 1 || calls[ids[1]] != 3 || calls[ids[2]] != 1 {
		t.Error("rejected preload accessed the private field")
	}
}
