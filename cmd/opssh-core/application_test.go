package main

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"errors"
	"fmt"
	"net"
	"path/filepath"
	"testing"
	"time"

	"golang.org/x/crypto/ssh"

	"opssh/internal/config"
	"opssh/internal/control"
	"opssh/internal/desktop"
)

type offlineVault struct {
	public       []byte
	fail         bool
	publicCalls  int
	privateCalls int
}

func (v *offlineVault) Public(context.Context, string, string) ([]byte, error) {
	v.publicCalls++
	if v.fail {
		return nil, errors.New("fake public lookup failed")
	}
	return v.public, nil
}
func (v *offlineVault) Private(context.Context, string, string) ([]byte, error) {
	v.privateCalls++
	return nil, errors.New("private lookup was unexpected")
}
func (*offlineVault) Status() string { return "unknown" }

type discoveryVault struct {
	offlineVault
	vaults   []desktop.Vault
	discover func(context.Context) error
	resolve  func(string, string) ([]byte, error)
}

func (v *discoveryVault) Discover(ctx context.Context, _ config.Scope) ([]desktop.Vault, error) {
	if v.discover != nil {
		return v.vaults, v.discover(ctx)
	}
	return v.vaults, nil
}

func (v *discoveryVault) Public(_ context.Context, vaultID, itemID string) ([]byte, error) {
	v.publicCalls++
	return v.resolve(vaultID, itemID)
}

func TestConnectDiscoversOnlyPublicKeysForTheEffectiveScope(t *testing.T) {
	const vaultID = "aaaaaaaaaaaaaaaaaaaaaaaaaa"
	const included = "bbbbbbbbbbbbbbbbbbbbbbbbbb"
	const excluded = "cccccccccccccccccccccccccc"
	public, _, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	key, err := ssh.NewPublicKey(public)
	if err != nil {
		t.Fatal(err)
	}
	store := &discoveryVault{
		vaults: []desktop.Vault{{ID: vaultID, Name: "Test vault", Items: []desktop.Item{
			{ID: included, Name: "Selected"}, {ID: excluded, Name: "Excluded"},
		}}},
		resolve: func(vault, item string) ([]byte, error) {
			if vault != vaultID || item != included {
				t.Errorf("resolved excluded or unknown reference %s/%s", vault, item)
			}
			return key.Marshal(), nil
		},
	}
	cfg := config.Default()
	cfg.Account = "Test"
	cfg.Scope.Items[vaultID+"/"+excluded] = "exclude"
	path := filepath.Join(t.TempDir(), "settings.json")
	app, err := newApplication(path, "fixture", cfg, nil, func(string) vault { return store })
	if err != nil {
		t.Fatal(err)
	}
	defer app.engine.Close()
	if store.publicCalls != 0 {
		t.Fatal("startup unexpectedly refreshed the index")
	}
	res := app.command(context.Background(), control.Event("connect", "connect", map[string]string{"account": "Test"}))
	if !res.OK {
		t.Fatalf("connect failed: %+v", res)
	}
	status := app.status()
	if status.Index != "ready" || len(status.Vaults) != 1 || len(status.Vaults[0].Items) != 2 {
		t.Fatalf("discovery metadata or status missing: %+v", status)
	}
	keys := app.engine.List()
	if len(keys) != 1 || keys[0].ItemID != included || store.publicCalls != 1 || store.privateCalls != 0 {
		t.Fatalf("unexpected index: %v, public=%d private=%d", keys, store.publicCalls, store.privateCalls)
	}
}

func TestScopeExclusionsPersistBeforeProviderFailure(t *testing.T) {
	const vaultID = "aaaaaaaaaaaaaaaaaaaaaaaaaa"
	const itemID = "bbbbbbbbbbbbbbbbbbbbbbbbbb"
	public, _, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	key, err := ssh.NewPublicKey(public)
	if err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(t.TempDir(), "settings.json")
	cfg := config.Default()
	cfg.Account = "Test"
	cfg.Keys = []config.Key{{VaultID: vaultID, ItemID: itemID, Public: key.Marshal()}}
	var app *application
	store := &discoveryVault{discover: func(context.Context) error {
		saved, err := config.Read(path)
		if err != nil || len(app.engine.List()) != 0 || len(saved.Keys) != 0 || saved.Scope.Includes(vaultID, itemID) {
			t.Errorf("exclusion was not effective before provider access: %+v, %v", saved, err)
		}
		return desktop.ErrUnavailable
	}}
	app, err = newApplication(path, "fixture", cfg, nil, func(string) vault { return store })
	if err != nil {
		t.Fatal(err)
	}
	defer app.engine.Close()
	res := app.command(context.Background(), control.Event("set-scope", "scope", map[string]string{"default": "exclude"}))
	if res.OK || app.status().Index != "unavailable" || len(app.engine.List()) != 0 {
		t.Fatalf("failed discovery restored an excluded key: %+v", res)
	}
}

func TestRefreshPublishesOnlyUnambiguousPublicKeysFromAPartialCatalog(t *testing.T) {
	const vaultID = "aaaaaaaaaaaaaaaaaaaaaaaaaa"
	const unique = "bbbbbbbbbbbbbbbbbbbbbbbbbb"
	const duplicated = "cccccccccccccccccccccccccc"
	const secondCopy = "dddddddddddddddddddddddddd"
	const missing = "eeeeeeeeeeeeeeeeeeeeeeeeee"
	newBlob := func() []byte {
		t.Helper()
		public, _, err := ed25519.GenerateKey(rand.Reader)
		if err != nil {
			t.Fatal(err)
		}
		key, err := ssh.NewPublicKey(public)
		if err != nil {
			t.Fatal(err)
		}
		return key.Marshal()
	}
	good, duplicate := newBlob(), newBlob()
	store := &discoveryVault{
		vaults: []desktop.Vault{{ID: vaultID, Items: []desktop.Item{
			{ID: unique}, {ID: duplicated}, {ID: secondCopy}, {ID: missing},
		}}},
		resolve: func(_, item string) ([]byte, error) {
			switch item {
			case unique:
				return good, nil
			case duplicated, secondCopy:
				return duplicate, nil
			default:
				return nil, desktop.ErrUnavailable
			}
		},
	}
	cfg := config.Default()
	cfg.Account = "Test"
	cfg.Keys = []config.Key{{VaultID: vaultID, ItemID: missing, Public: newBlob()}}
	path := filepath.Join(t.TempDir(), "settings.json")
	app, err := newApplication(path, "fixture", cfg, nil, func(string) vault { return store })
	if err != nil {
		t.Fatal(err)
	}
	defer app.engine.Close()
	res := app.command(context.Background(), control.Event("refresh-index", "refresh", nil))
	if res.OK || app.status().Index != "partial" {
		t.Fatalf("incomplete refresh reported success: %+v", res)
	}
	keys := app.engine.List()
	saved, err := config.Read(path)
	if len(keys) != 1 || keys[0].ItemID != unique || err != nil || saved.Index != "partial" || store.privateCalls != 0 {
		t.Fatalf("partial refresh exposed stale or ambiguous keys: %v, %v", keys, err)
	}
}

func TestConnectReplacementClearsPriorAccountBeforeRestart(t *testing.T) {
	cfg := config.Default()
	cfg.Account = "Original"
	cfg.Scope.Default = "exclude"
	cfg.Scope.Items[cfgKeyID()] = "include"
	path := filepath.Join(t.TempDir(), "settings.json")
	var accounts []string
	app, err := newApplication(path, "fixture", cfg, nil, func(account string) vault {
		accounts = append(accounts, account)
		return &offlineVault{}
	})
	if err != nil {
		t.Fatal(err)
	}
	defer app.engine.Close()
	result := app.command(context.Background(), control.Event("connect", "connect", map[string]string{"account": "Replacement"}))
	saved, err := config.Read(path)
	if !result.OK || !result.RestartRequired || err != nil || saved.Account != "Replacement" ||
		saved.Scope.Default != "include" || len(saved.Scope.Items) != 0 || saved.Index != "not-indexed" {
		t.Fatalf("replacement account did not start with fresh scope: %+v, %+v, %v", result, saved, err)
	}
	if len(accounts) != 1 || accounts[0] != "Original" || !app.engine.Status().Locked {
		t.Fatal("core opened the replacement account before the restart")
	}
}

func TestOversizedScopeDoesNotResolveOrAdvertiseATruncatedIndex(t *testing.T) {
	const vaultID = "aaaaaaaaaaaaaaaaaaaaaaaaaa"
	items := make([]desktop.Item, 65)
	for i := range items {
		items[i] = desktop.Item{ID: fmt.Sprintf("%026d", i)}
	}
	store := &discoveryVault{vaults: []desktop.Vault{{ID: vaultID, Items: items}}}
	cfg := config.Default()
	cfg.Account = "Test"
	path := filepath.Join(t.TempDir(), "settings.json")
	app, err := newApplication(path, "fixture", cfg, nil, func(string) vault { return store })
	if err != nil {
		t.Fatal(err)
	}
	defer app.engine.Close()
	res := app.command(context.Background(), control.Event("refresh-index", "refresh", nil))
	status := app.status()
	if res.OK || status.Index != "scope-too-broad" || len(status.Keys) != 0 || store.publicCalls != 0 {
		t.Fatalf("oversized catalog was advertised: %+v", res)
	}
	if len(status.Vaults) != 1 || len(status.Vaults[0].Items) != len(items) {
		t.Fatalf("metadata missing for scope narrowing: %+v", status.Vaults)
	}
}

func TestRefreshFailureRetiresPublicIndex(t *testing.T) {
	public, _, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	key, err := ssh.NewPublicKey(public)
	if err != nil {
		t.Fatal(err)
	}
	store := &offlineVault{public: key.Marshal()}
	cfg := config.Default()
	cfg.Account = "Test"
	path := filepath.Join(t.TempDir(), "settings.json")
	app, err := newApplication(path, "fixture", cfg, nil, func(string) vault { return store })
	if err != nil {
		t.Fatal(err)
	}
	defer app.engine.Close()
	res := app.command(context.Background(), control.Event("add-key", "1", map[string]string{
		"vaultId": "aaaaaaaaaaaaaaaaaaaaaaaaaa", "itemId": "bbbbbbbbbbbbbbbbbbbbbbbbbb", "name": "Offline",
	}))
	if !res.OK || len(app.engine.List()) != 1 || store.publicCalls != 1 || store.privateCalls != 0 {
		t.Fatalf("add key result: %+v, public=%d private=%d", res, store.publicCalls, store.privateCalls)
	}
	store.fail = true
	res = app.command(context.Background(), control.Event("refresh", "2", map[string]string{"keyId": cfgKeyID()}))
	if res.OK || len(app.engine.List()) != 0 {
		t.Fatalf("failed refresh advertised an old key: %+v", res)
	}
	saved, err := config.Read(path)
	if err != nil || len(saved.Keys) != 1 || len(saved.Keys[0].Public) != 0 {
		t.Fatalf("failed refresh did not persist a disabled public index: %v", err)
	}
}

func cfgKeyID() string { return "aaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbbbbbbbbbbbb" }

func TestAccountChangePreventsUnlockBeforeRestart(t *testing.T) {
	cfg := config.Default()
	cfg.Account = "Initial"
	path := filepath.Join(t.TempDir(), "settings.json")
	app, err := newApplication(path, "fixture", cfg, nil, func(string) vault { return &offlineVault{} })
	if err != nil {
		t.Fatal(err)
	}
	defer app.engine.Close()
	result := app.command(context.Background(), control.Event("settings", "account", map[string]any{
		"account": "Replacement", "keyCache": cfg.KeyCache, "approvalCache": cfg.ApprovalCache,
	}))
	if !result.OK || !result.RestartRequired {
		t.Fatalf("account change result: %+v", result)
	}
	result, handled := app.priority(control.Event("unlock", "unlock", nil))
	if !handled || result.OK || !result.RestartRequired || !app.engine.Status().Locked {
		t.Fatalf("unlock bypassed account restart: %+v", result)
	}
}

func TestLockAndUnlockStayOrderedWhileCommandWorkerIsBusy(t *testing.T) {
	server, gui := net.Pipe()
	t.Cleanup(func() { gui.Close(); server.Close() })
	if err := gui.SetDeadline(time.Now().Add(2 * time.Second)); err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	ch := control.New(server)
	app, err := newApplication("", "fixture", config.Default(), ch, func(string) vault { return &offlineVault{} })
	if err != nil {
		t.Fatal(err)
	}
	defer app.engine.Close()
	app.engine.Lock()
	workerStarted := make(chan struct{})
	go ch.Run(ctx, func(ctx context.Context, msg control.Message) control.Result {
		if msg.Type == "blocking-command" {
			close(workerStarted)
			<-ctx.Done()
			return control.Result{}
		}
		return app.command(ctx, msg)
	}, app.priority)
	if err := control.Write(gui, control.Event("blocking-command", "provider", nil)); err != nil {
		t.Fatal(err)
	}
	select {
	case <-workerStarted:
	case <-ctx.Done():
		t.Fatal("command worker did not start")
	}
	for _, command := range []string{"unlock", "lock"} {
		if err := control.Write(gui, control.Event(command, command, nil)); err != nil {
			t.Fatal(err)
		}
		reply, err := control.Read(gui)
		var result control.Result
		if err != nil || reply.Type != "result" || reply.ID != command || control.DecodeBody(reply, &result) != nil || !result.OK {
			t.Fatalf("%s did not finish in order while provider was busy: %+v, %v", command, reply, err)
		}
		if app.engine.Status().Locked != (command == "lock") {
			t.Fatalf("unexpected lock state after %s", command)
		}
	}
}
