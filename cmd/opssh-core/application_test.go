package main

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"errors"
	"net"
	"path/filepath"
	"testing"
	"time"

	"golang.org/x/crypto/ssh"

	"opssh/internal/config"
	"opssh/internal/control"
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
