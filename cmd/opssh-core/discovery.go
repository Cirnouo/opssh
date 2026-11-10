package main

import (
	"context"
	"errors"
	"maps"
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

type discoveryReporter interface {
	DiscoverWithProgress(context.Context, config.Scope, func(int, int)) ([]desktop.Vault, error)
}

type publicBatchReader interface {
	PublicBatch(context.Context, []config.Key) ([][]byte, error)
}

func (a *application) refreshIndex(ctx context.Context, next config.Settings) error {
	ctx, cancel := context.WithTimeout(ctx, indexTimeout)
	defer cancel()
	return a.scanIndex(ctx, next, nil)
}

func (a *application) scanIndex(ctx context.Context, next config.Settings, op *discoveryOperation) (result error) {
	if next.Account == "" {
		return desktop.ErrUnconfigured
	}

	// As with single-item refresh, retire old identities before reading from
	// the provider. Persist any new scope in the same update so restarting
	// cannot restore keys the user just excluded.
	previous := a.settings
	next.Keys = []config.Key{}
	next.Index = config.IndexUnavailable
	published := false
	if op != nil {
		a.engine.SuspendIndex()
		defer a.engine.ResumeIndex()
		if err := config.Save(a.path, next); err != nil {
			return errSettingsWrite
		}
		withdrawn := next
		defer func() {
			_ = op.finish()
			if published {
				return
			}
			if errors.Is(context.Cause(ctx), errDiscoveryCanceled) {
				result = a.restoreDiscoveryIndex(previous, withdrawn)
				if errors.Is(result, errDiscoveryCanceled) {
					return
				}
			}
			// A timeout or provider error leaves the durable index withdrawn.
			_ = a.engine.ReplaceKeys(nil)
			a.settings = withdrawn
		}()
	} else if err := a.persist(next); err != nil {
		return err
	}
	provider, ok := a.vault.get().(discoverableVault)
	if !ok {
		return desktop.ErrUnavailable
	}
	var vaults []desktop.Vault
	var discoverErr error
	if reporter, ok := provider.(discoveryReporter); op != nil && ok {
		vaults, discoverErr = reporter.DiscoverWithProgress(ctx, next.Scope, func(completed, total int) {
			op.report("vaults", completed, total)
		})
	} else {
		vaults, discoverErr = provider.Discover(ctx, next.Scope)
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	partial := errors.Is(discoverErr, desktop.ErrPartial)
	overflow := errors.Is(discoverErr, desktop.ErrScopeTooBroad) || errors.Is(discoverErr, desktop.ErrScanLimit)
	if discoverErr != nil && !partial && !overflow {
		return discoverErr
	}
	if !overflow {
		next.Keys, overflow = scopedKeys(next.Scope, vaults)
	}
	if overflow {
		next.Index = config.IndexScopeTooBroad
	} else {
		if op != nil {
			op.report("public", 0, len(next.Keys))
		}
		complete, err := readPublicIndex(ctx, provider, next.Keys)
		if errors.Is(err, desktop.ErrSessionExpired) {
			return err
		}
		partial = partial || !complete
		next.Index = config.IndexReady
		if partial {
			next.Index = config.IndexPartial
		} else if op != nil {
			next.Vaults = publicVaults(vaults)
		}
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	if op != nil {
		if err := op.finish(); err != nil {
			return err
		}
		if next.Index == config.IndexPartial {
			_ = a.engine.ReplaceKeys(nil)
		}
	}
	if err := a.persist(next); err != nil {
		return err
	}
	published = true
	a.setVaults(vaults)
	if overflow {
		if errors.Is(discoverErr, desktop.ErrScanLimit) {
			return desktop.ErrScanLimit
		}
		return desktop.ErrScopeTooBroad
	}
	// An excluded vault's metadata is for the legacy item scope editor. Its availability
	// cannot block or change the result of publishing the effective keys.
	if op == nil {
		if browser, ok := provider.(excludedVaultBrowser); ok {
			a.browseExcluded(ctx, browser, next.Scope, vaults)
		}
	}
	if partial {
		return desktop.ErrPartial
	}
	return nil
}

func (a *application) restoreDiscoveryIndex(previous, withdrawn config.Settings) error {
	current, err := config.Read(a.path)
	if err != nil {
		return errSettingsWrite
	}
	if current.Account != withdrawn.Account || current.AccountName != withdrawn.AccountName ||
		current.Index != config.IndexUnavailable || len(current.Keys) != 0 ||
		current.KeyCache != withdrawn.KeyCache || current.ApprovalCache != withdrawn.ApprovalCache ||
		!slices.Equal(current.Vaults, withdrawn.Vaults) || current.Scope.Default != withdrawn.Scope.Default ||
		!maps.Equal(current.Scope.Vaults, withdrawn.Scope.Vaults) || !maps.Equal(current.Scope.Items, withdrawn.Scope.Items) {
		return errAccountChanged
	}
	if err := a.persist(previous); err != nil {
		return err
	}
	return errDiscoveryCanceled
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

func readPublicIndex(ctx context.Context, provider vault, keys []config.Key) (bool, error) {
	blobs := make([][]byte, len(keys))
	var batchErr error
	if bulk, ok := provider.(publicBatchReader); ok {
		var resolved [][]byte
		resolved, batchErr = bulk.PublicBatch(ctx, keys)
		if len(resolved) == len(keys) {
			blobs = resolved
		}
	} else {
		for i, key := range keys {
			if err := ctx.Err(); err != nil {
				return false, err
			}
			public, err := provider.Public(ctx, key.VaultID, key.ItemID)
			if errors.Is(err, desktop.ErrSessionExpired) {
				return false, err
			}
			if err == nil {
				blobs[i] = public
			}
		}
	}
	first := make(map[string]int, len(keys))
	complete := batchErr == nil
	for i, public := range blobs {
		if !engine.ValidPublic(public) {
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
	return complete, batchErr
}
