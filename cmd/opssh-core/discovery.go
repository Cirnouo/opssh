package main

import (
	"context"
	"errors"
	"slices"
	"time"

	"opssh/internal/config"
	"opssh/internal/desktop"
	"opssh/internal/engine"
)

const indexTimeout = 30 * time.Second

type excludedVaultBrowser interface {
	Browse(context.Context, config.Scope, []desktop.Vault) ([]desktop.Vault, error)
}

func (a *application) refreshIndex(ctx context.Context, next config.Settings) error {
	return a.scanIndex(ctx, next, false)
}

func (a *application) scanIndex(ctx context.Context, next config.Settings, preserve bool) error {
	if next.Account == "" {
		return desktop.ErrUnconfigured
	}
	ctx, cancel := context.WithTimeout(ctx, indexTimeout)
	defer cancel()

	// As with single-item refresh, retire old identities before reading from
	// the provider. Persist any new scope in the same update so restarting
	// cannot restore keys the user just excluded.
	next.Keys = []config.Key{}
	next.Index = config.IndexUnavailable
	published := false
	if preserve {
		a.engine.SuspendIndex()
		defer a.engine.ResumeIndex()
		if err := config.Save(a.path, next); err != nil {
			return errSettingsWrite
		}
		withdrawn := next
		defer func() {
			if !published {
				// The durable index was already withdrawn. Do not republish
				// a previous identity after cancellation or a provider error.
				_ = a.engine.ReplaceKeys(nil)
				a.settings = withdrawn
			}
		}()
	} else if err := a.persist(next); err != nil {
		return err
	}
	provider, ok := a.vault.get().(discoverableVault)
	if !ok {
		return desktop.ErrUnavailable
	}
	vaults, discoverErr := provider.Discover(ctx, next.Scope)
	if err := ctx.Err(); err != nil {
		return err
	}
	partial := errors.Is(discoverErr, desktop.ErrPartial)
	overflow := errors.Is(discoverErr, desktop.ErrScopeTooBroad) || errors.Is(discoverErr, desktop.ErrScanLimit)
	if discoverErr != nil && !partial && !overflow {
		return discoverErr
	}
	a.setVaults(vaults)
	if preserve {
		next.Vaults = publicVaults(a.vaults)
	}
	if !overflow {
		next.Keys, overflow = scopedKeys(next.Scope, vaults)
	}
	if overflow {
		next.Index = config.IndexScopeTooBroad
		if err := a.persist(next); err != nil {
			return err
		}
		published = true
		if errors.Is(discoverErr, desktop.ErrScanLimit) {
			return desktop.ErrScanLimit
		}
		return desktop.ErrScopeTooBroad
	}

	if !readPublicIndex(ctx, provider, next.Keys) {
		partial = true
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	next.Index = config.IndexReady
	if partial {
		next.Index = config.IndexPartial
		if preserve {
			_ = a.engine.ReplaceKeys(nil)
		}
	}
	if err := a.persist(next); err != nil {
		return err
	}
	published = true
	// An excluded vault's metadata is for the legacy item scope editor. Its availability
	// cannot block or change the result of publishing the effective keys.
	if !preserve {
		if browser, ok := provider.(excludedVaultBrowser); ok {
			a.browseExcluded(ctx, browser, next.Scope, vaults)
		}
	}
	if partial {
		return desktop.ErrPartial
	}
	return nil
}

func (a *application) browseExcluded(ctx context.Context, browser excludedVaultBrowser, scope config.Scope, vaults []desktop.Vault) {
	if !slices.ContainsFunc(vaults, func(v desktop.Vault) bool { return v.Items == nil }) {
		return
	}
	ctx, cancel := context.WithTimeout(ctx, 2*time.Second)
	defer cancel()
	if rows, _ := browser.Browse(ctx, scope, vaults); rows != nil {
		a.setVaults(rows)
	}
}

func (a *application) setVaults(vaults []desktop.Vault) {
	a.vaults = append([]desktop.Vault{}, vaults...)
	for i := range a.vaults {
		if a.vaults[i].Items == nil {
			a.vaults[i].Items = []desktop.Item{}
		}
	}
}

func scopedKeys(scope config.Scope, vaults []desktop.Vault) ([]config.Key, bool) {
	keys := make([]config.Key, 0, config.MaxKeys)
	for _, vault := range vaults {
		for _, item := range vault.Items {
			if scope.Includes(vault.ID, item.ID) {
				if len(keys) == config.MaxKeys {
					return []config.Key{}, true
				}
				keys = append(keys, config.Key{VaultID: vault.ID, ItemID: item.ID, Name: item.Name})
			}
		}
	}
	return keys, false
}

func readPublicIndex(ctx context.Context, provider vault, keys []config.Key) bool {
	first := make(map[string]int, len(keys))
	complete := true
	for i, key := range keys {
		if ctx.Err() != nil {
			return false
		}
		public, err := provider.Public(ctx, key.VaultID, key.ItemID)
		if err != nil || !engine.ValidPublic(public) {
			complete = false
			continue
		}
		if prior, duplicate := first[string(public)]; duplicate {
			// A public blob must identify a single private lookup.
			keys[prior].Public = nil
			complete = false
			continue
		}
		first[string(public)] = i
		keys[i].Public = public
	}
	return complete
}
