package main

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"encoding/json"
	"encoding/pem"
	"errors"
	"path/filepath"
	"slices"
	"strings"
	"testing"

	"golang.org/x/crypto/ssh"

	"opssh/internal/config"
	"opssh/internal/control"
	"opssh/internal/desktop"
	"opssh/internal/engine"
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
	if _, ok := app.diagnostics.Sync(true, 1, 0); !ok {
		t.Fatal("could not enable diagnostics for the fixture")
	}
	inspect := func(result control.Result, selected, loaded, alreadyLoaded, failed, skipped int, code string, steps ...string) {
		t.Helper()
		body, err := json.Marshal(result)
		if err != nil || strings.Contains(string(body), opaqueError) {
			t.Fatal("private result exposed unsanitized provider data")
		}
		var wire struct {
			Data struct {
				engine.PreloadReport
				Steps      json.RawMessage `json:"steps"`
				StepsEpoch uint64          `json:"stepsEpoch"`
			} `json:"data"`
		}
		want := engine.PreloadReport{Selected: selected, Loaded: loaded, AlreadyLoaded: alreadyLoaded, Failed: failed, Skipped: skipped}
		if json.Unmarshal(body, &wire) != nil || wire.Data.PreloadReport != want || result.OK != (code == "") || result.ErrorCode != code {
			t.Errorf("unexpected preload result: ok=%v, code=%q, counts=%+v", result.OK, result.ErrorCode, wire.Data.PreloadReport)
		}
		if len(steps) != 0 {
			if wire.Data.StepsEpoch != 1 {
				t.Error("preload steps did not identify the diagnostic consent epoch")
			}
			checkDiagnosticSteps(t, wire.Data.Steps, steps...)
		}
	}
	result = app.command(t.Context(), control.Event("preload-all", "batch", nil))
	inspect(result, 3, 1, 0, 2, 0, "preload-partial", "request/accepted", "key-cache/miss", "provider-read/session-expired")
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
	inspect(result, 1, 0, 1, 0, 0, "", "request/accepted", "key-cache/hit")
	if calls[ids[0]] != 1 {
		t.Error("preloading a loaded key read its private field again")
	}
	result = app.command(t.Context(), control.Event("preload", "expired", map[string]string{"keyId": vaultID + "/" + ids[1]}))
	inspect(result, 1, 0, 0, 1, 0, "desktop-session-expired", "request/accepted", "key-cache/miss", "provider-read/session-expired")
	result = app.command(t.Context(), control.Event("preload", "opaque", map[string]string{"keyId": vaultID + "/" + ids[2]}))
	inspect(result, 1, 0, 0, 1, 0, "private-lookup", "request/accepted", "key-cache/miss", "provider-read/unavailable")
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
	if calls[ids[0]] != 1 || calls[ids[1]] != 3 || calls[ids[2]] != 2 {
		t.Error("rejected preload accessed the private field")
	}
}

func checkDiagnosticSteps(t *testing.T, body json.RawMessage, want ...string) {
	t.Helper()
	var steps []struct {
		Stage     string `json:"stage"`
		Outcome   string `json:"outcome"`
		ElapsedMs *int64 `json:"elapsedMs"`
	}
	decoder := json.NewDecoder(bytes.NewReader(body))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&steps); err != nil {
		t.Errorf("diagnostic steps are absent or contain unexpected fields, want %v", want)
		return
	}
	got := make([]string, 0, len(steps))
	for _, step := range steps {
		if step.ElapsedMs == nil || *step.ElapsedMs < 0 || *step.ElapsedMs > 86_400_000 {
			t.Error("diagnostic step did not contain a bounded duration")
		}
		got = append(got, step.Stage+"/"+step.Outcome)
	}
	if !slices.Equal(got, want) {
		t.Errorf("diagnostic steps did not match %v", want)
	}
}
