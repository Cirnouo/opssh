package main

import (
	"context"
	"path/filepath"
	"testing"

	"opssh/internal/config"
	"opssh/internal/control"
	"opssh/internal/desktop"
)

const candidateAccount = "aaaaaaaaaaaaaaaaaaaaaaaaaa"

type catalogOnlyVault struct {
	offlineVault
	vaults []desktop.Vault
	err    error
}

func (v *catalogOnlyVault) ListVaults(context.Context) ([]desktop.Vault, error) {
	return v.vaults, v.err
}

func TestCandidateAuthorizesWithoutReplacingSavedAccount(t *testing.T) {
	path := filepath.Join(t.TempDir(), "settings.json")
	old := config.Default()
	old.Account = "Old account"
	if err := config.Save(path, old); err != nil {
		t.Fatal(err)
	}
	store := &catalogOnlyVault{vaults: []desktop.Vault{{ID: candidateAccount, Name: "Offline vault"}}}
	app, err := newApplication(path, "fixture", old, nil, func(string) vault { return store })
	if err != nil {
		t.Fatal(err)
	}
	defer app.engine.Close()
	app.candidate = true
	ctx := context.Background()
	result := app.command(ctx, control.Event("prepare-account", "prepare", accountChoice{Account: candidateAccount, Name: "Test account"}))
	if !result.OK {
		t.Fatalf("prepare: %+v", result)
	}
	saved, err := config.Read(path)
	if err != nil || saved.Account != old.Account || len(saved.Vaults) != 0 || store.publicCalls != 0 || store.privateCalls != 0 {
		t.Fatalf("prepare modified the active account: %+v %v", saved, err)
	}
	if err := app.activateAccount(ctx); err != nil {
		t.Fatal(err)
	}
	saved, err = config.Read(path)
	if err != nil || saved.Account != candidateAccount || len(saved.Vaults) != 1 ||
		saved.Scope.Default != "exclude" || len(saved.Keys) != 0 {
		t.Fatalf("activation: %+v %v", saved, err)
	}
}

func TestVaultScopeEnableAndDisableNeverReadsKeys(t *testing.T) {
	path := filepath.Join(t.TempDir(), "settings.json")
	cfg := config.Default()
	cfg.Account = candidateAccount
	cfg.Scope.Default = "exclude"
	cfg.Vaults = []config.Vault{{ID: candidateAccount, Name: "Offline vault"}}
	store := &catalogOnlyVault{}
	app, err := newApplication(path, "fixture", cfg, nil, func(string) vault { return store })
	if err != nil {
		t.Fatal(err)
	}
	defer app.engine.Close()
	for _, enabled := range []bool{true, false} {
		msg := control.Event("set-vault", "scope", map[string]any{"vaultId": candidateAccount, "enabled": enabled})
		result := app.command(context.Background(), msg)
		if !result.OK || app.settings.Scope.IncludesVault(candidateAccount) != enabled {
			t.Fatalf("set-vault: %+v", result)
		}
	}
	if store.publicCalls != 0 || store.privateCalls != 0 {
		t.Fatal("editing scope unexpectedly read provider keys")
	}
}
