package main

import (
	"context"
	"errors"
	"slices"
	"strings"

	"opssh/internal/config"
	"opssh/internal/control"
	"opssh/internal/desktop"
)

type accountChoice struct {
	Account string `json:"account"`
	Name    string `json:"name"`
}

var (
	errVaultUnavailable = errors.New("vault is no longer available")
	errVaultScopeLimit  = errors.New("vault scope limit reached")
)

func validAccountUUID(value string) bool {
	if len(value) != 26 {
		return false
	}
	for _, ch := range value {
		if !(ch >= 'A' && ch <= 'Z') && !(ch >= 'a' && ch <= 'z') && !(ch >= '0' && ch <= '9') {
			return false
		}
	}
	return true
}

func sameAccount(left, right string) bool {
	return left == right || validAccountUUID(left) && validAccountUUID(right) && strings.EqualFold(left, right)
}

type vaultCatalog interface {
	ListVaults(context.Context) ([]desktop.Vault, error)
}

func listVaults(ctx context.Context, provider vault) ([]desktop.Vault, error) {
	catalog, ok := provider.(vaultCatalog)
	if !ok {
		return nil, desktop.ErrUnavailable
	}
	ctx, cancel := context.WithTimeout(ctx, indexTimeout)
	defer cancel()
	rows, err := catalog.ListVaults(ctx)
	if err != nil {
		return nil, err
	}
	return rows, ctx.Err()
}

func publicVaults(rows []desktop.Vault) []config.Vault {
	result := make([]config.Vault, 0, len(rows))
	for _, row := range rows {
		result = append(result, config.Vault{ID: row.ID, Name: row.Name})
	}
	return result
}

func savedVaults(rows []config.Vault) []desktop.Vault {
	result := make([]desktop.Vault, 0, len(rows))
	for _, row := range rows {
		result = append(result, desktop.Vault{ID: row.ID, Name: row.Name, Items: []desktop.Item{}})
	}
	return result
}

func (a *application) prepareAccount(ctx context.Context, msg control.Message) error {
	var choice accountChoice
	if a.attempted || control.DecodeBody(msg, &choice) != nil ||
		!validAccountUUID(choice.Account) || !config.ValidLabel(choice.Name) || choice.Name == "" ||
		sameAccount(choice.Account, a.settings.Account) {
		return errCommand
	}
	// Desktop integration can pin the account in this process on first use.
	a.attempted = true
	provider := a.newVault(choice.Account)
	rows, err := listVaults(ctx, provider)
	if err != nil {
		return err
	}
	next := a.copySettings()
	next.Account, next.AccountName = choice.Account, choice.Name
	next.Keys, next.Index = []config.Key{}, config.IndexNotIndexed
	next.Scope = config.Scope{Default: "exclude"}.Normalized()
	next.Vaults = publicVaults(rows)
	if err := next.Validate(); err != nil {
		return err
	}
	a.vault.set(provider)
	a.prepared = &next
	return nil
}

func (a *application) updateAccountLabel(msg control.Message) error {
	var choice accountChoice
	if control.DecodeBody(msg, &choice) != nil || !validAccountUUID(choice.Account) ||
		!config.ValidLabel(choice.Name) || choice.Name == "" {
		return errCommand
	}
	if !sameAccount(choice.Account, a.settings.Account) {
		return errAccountChanged
	}
	next := a.copySettings()
	next.AccountName = choice.Name
	return a.persist(next)
}

func (a *application) activateAccount(ctx context.Context) error {
	if !a.candidate || a.prepared == nil {
		return errCommand
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	current, err := config.Read(a.path)
	if err != nil || current.Account != a.settings.Account {
		return errAccountChanged
	}
	next := *a.prepared
	next.KeyCache, next.ApprovalCache = current.KeyCache, current.ApprovalCache
	if err := a.persist(next); err != nil {
		return err
	}
	a.vaults = savedVaults(next.Vaults)
	a.prepared = nil
	a.candidate = false
	return nil
}

func (a *application) reloadVaults(ctx context.Context) error {
	if a.settings.Account == "" {
		return desktop.ErrUnconfigured
	}
	rows, err := listVaults(ctx, a.vault.get())
	if err != nil {
		return err
	}
	next := a.copySettings()
	next.Vaults = publicVaults(rows)
	if err := a.persist(next); err != nil {
		return err
	}
	a.vaults = savedVaults(next.Vaults)
	return nil
}

func (a *application) setVault(msg control.Message) error {
	var form struct {
		VaultID string `json:"vaultId"`
		Enabled *bool  `json:"enabled"`
	}
	if a.settings.Account == "" || control.DecodeBody(msg, &form) != nil ||
		!config.ValidID(form.VaultID) || form.Enabled == nil {
		return errCommand
	}
	next := a.copySettings()
	known := slices.ContainsFunc(next.Vaults, func(v config.Vault) bool { return v.ID == form.VaultID })
	_, overridden := next.Scope.Vaults[form.VaultID]
	legacyItem := false
	for id := range next.Scope.Items {
		if strings.HasPrefix(id, form.VaultID+"/") {
			legacyItem = true
			delete(next.Scope.Items, id)
		}
	}
	if !known && (*form.Enabled || (!overridden && !legacyItem)) {
		return errVaultUnavailable
	}
	rule := "exclude"
	if *form.Enabled {
		rule = "include"
	}
	if rule == next.Scope.Default {
		delete(next.Scope.Vaults, form.VaultID)
	} else {
		next.Scope.Vaults[form.VaultID] = rule
	}
	if len(next.Scope.Vaults) > config.MaxVaults {
		return errVaultScopeLimit
	}
	next.Keys = slices.DeleteFunc(next.Keys, func(k config.Key) bool { return !next.Scope.Includes(k.VaultID, k.ItemID) })
	return a.persist(next)
}
