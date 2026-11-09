package desktop

import (
	"context"
	"errors"
	"runtime"
	"slices"
	"strings"
	"unicode"
	"unicode/utf8"

	onepassword "github.com/1password/onepassword-sdk-go"

	"opssh/internal/config"
)

var (
	ErrPartial       = errors.New("desktop catalog was incomplete")
	ErrScopeTooBroad = errors.New("desktop scope selected too many keys")
	ErrScanLimit     = errors.New("desktop catalog exceeded a scan limit")
)

const (
	// Keep worst-case JSON (including escaping) of metadata, scope and key
	// status within the 256 KiB control frame.
	maxDiscoveryLabel = 80
	maxScannedVaults  = 256
	maxScannedItems   = 8192
)

type Vault struct {
	ID    string `json:"id"`
	Name  string `json:"name"`
	Items []Item `json:"items"`
}

type Item struct {
	ID   string `json:"id"`
	Name string `json:"name"`
}

type metadataLister interface {
	Vaults(context.Context) ([]onepassword.VaultOverview, error)
	Items(context.Context, string) ([]onepassword.ItemOverview, error)
}

func (c desktopClient) Vaults(ctx context.Context) ([]onepassword.VaultOverview, error) {
	vaults, err := c.client.Vaults().List(ctx)
	runtime.KeepAlive(c.client)
	return vaults, err
}

func (c desktopClient) Items(ctx context.Context, vault string) ([]onepassword.ItemOverview, error) {
	active := onepassword.NewItemListFilterTypeVariantByState(&onepassword.ItemListFilterByStateInner{Active: true})
	items, err := c.client.Items().List(ctx, vault, active)
	runtime.KeepAlive(c.client)
	return items, err
}

// ListVaults requests only overview metadata, allowing account authorization
// and vault selection without reading items or private material.
func (b *Backend) ListVaults(ctx context.Context) ([]Vault, error) {
	return execute(ctx, b, func(client resolver) ([]Vault, error) {
		lister, ok := client.(metadataLister)
		if !ok {
			return nil, ErrUnavailable
		}
		listed, err := lister.Vaults(ctx)
		if err != nil {
			return nil, ErrUnavailable
		}
		if len(listed) > maxScannedVaults {
			return nil, ErrScanLimit
		}
		counts := make(map[string]int, len(listed))
		for _, vault := range listed {
			counts[vault.ID]++
		}
		rows := make([]Vault, 0, min(len(listed), config.MaxVaults))
		for _, vault := range listed {
			if !config.ValidID(vault.ID) || counts[vault.ID] != 1 {
				continue
			}
			if len(rows) == config.MaxVaults {
				break
			}
			rows = append(rows, Vault{ID: vault.ID, Name: safeName(vault.Title, "Vault "+vault.ID[:8]), Items: []Item{}})
		}
		return rows, ctx.Err()
	}, nil)
}

func (b *Backend) Discover(ctx context.Context, scope config.Scope) ([]Vault, error) {
	return execute(ctx, b, func(client resolver) ([]Vault, error) {
		lister, ok := client.(metadataLister)
		if !ok {
			return nil, ErrUnavailable
		}
		return discover(ctx, lister, scope)
	}, nil)
}

// Browse fills metadata for vaults that cannot contain an effective key.
// The caller can publish the public index before starting this optional work.
func (b *Backend) Browse(ctx context.Context, scope config.Scope, vaults []Vault) ([]Vault, error) {
	remaining := config.MaxItems
	for _, vault := range vaults {
		remaining -= len(vault.Items)
	}
	rows := slices.Clone(vaults)
	for i, row := range rows {
		if row.Items != nil || remaining == 0 {
			continue
		}
		limit := remaining
		items, err := execute(ctx, b, func(client resolver) ([]Item, error) {
			lister, ok := client.(metadataLister)
			if !ok {
				return nil, ErrUnavailable
			}
			listed, err := lister.Items(ctx, row.ID)
			if err != nil {
				return nil, ErrUnavailable
			}
			if err := ctx.Err(); err != nil {
				return nil, err
			}
			groups, _, _, _ := activeSSHItems(listed, row.ID, scope)
			items := make([]Item, 0, limit)
			for _, group := range groups {
				count := min(len(group), limit-len(items))
				items = append(items, group[:count]...)
			}
			return items, nil
		}, nil)
		if err != nil {
			if ctx.Err() != nil || errors.Is(err, ErrBusy) {
				return rows, err
			}
			continue
		}
		rows[i].Items = items
		remaining -= len(items)
	}
	return rows, nil
}

type catalogVault struct {
	vault    Vault
	groups   [3][]Item
	priority int
}

func discover(ctx context.Context, lister metadataLister, scope config.Scope) ([]Vault, error) {
	listed, err := lister.Vaults(ctx)
	if err != nil {
		return nil, ErrUnavailable
	}
	scanLimit := len(listed) > maxScannedVaults
	listed = listed[:min(len(listed), maxScannedVaults)]
	counts := make(map[string]int, len(listed))
	for _, vault := range listed {
		counts[vault.ID]++
	}
	explicit := make(map[string]bool, len(scope.Vaults))
	includedItems := make(map[string]bool)
	for id := range scope.Vaults {
		explicit[id] = true
	}
	for id, rule := range scope.Items {
		vault, _, _ := strings.Cut(id, "/")
		explicit[vault] = true
		if rule == "include" {
			includedItems[vault] = true
		}
	}
	var partial bool
	effective := 0
	vaults := make([]catalogVault, 0, len(listed))
	for _, vault := range listed {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		eligible := scope.IncludesVault(vault.ID) || includedItems[vault.ID]
		if !config.ValidID(vault.ID) || counts[vault.ID] != 1 {
			partial = partial || eligible
			continue
		}
		row := catalogVault{vault: Vault{ID: vault.ID, Name: safeName(vault.Title, "Vault "+vault.ID[:8])}, priority: 2}
		if explicit[vault.ID] {
			row.priority = 1
		}
		if eligible {
			row.vault.Items = []Item{}
			items, err := lister.Items(ctx, vault.ID)
			if err != nil {
				partial = true
			} else {
				var selected int
				var incomplete, tooMany bool
				row.groups, selected, incomplete, tooMany = activeSSHItems(items, vault.ID, scope)
				if selected > 0 {
					row.priority = 0
				}
				effective += selected
				partial = partial || incomplete
				scanLimit = scanLimit || tooMany
			}
		}
		vaults = append(vaults, row)
	}
	display := displayVaults(vaults)
	if scanLimit {
		return display, ErrScanLimit
	}
	if effective > config.MaxKeys {
		return display, ErrScopeTooBroad
	}
	if partial {
		return display, ErrPartial
	}
	return display, nil
}

func displayVaults(vaults []catalogVault) []Vault {
	slices.SortStableFunc(vaults, func(a, b catalogVault) int { return a.priority - b.priority })
	vaults = vaults[:min(len(vaults), config.MaxVaults)]
	rows := make([]Vault, len(vaults))
	for i, vault := range vaults {
		rows[i] = vault.vault
	}
	remaining := config.MaxItems
	// Allocate the shared metadata budget to included items before overrides
	// and inherited exclusions, even if they live in different vaults.
	for rank := range 3 {
		for i, vault := range vaults {
			group := vault.groups[rank]
			count := min(len(group), remaining)
			rows[i].Items = append(rows[i].Items, group[:count]...)
			remaining -= count
		}
	}
	return rows
}

func activeSSHItems(listed []onepassword.ItemOverview, vaultID string, scope config.Scope) ([3][]Item, int, bool, bool) {
	overflow := len(listed) > maxScannedItems
	listed = listed[:min(len(listed), maxScannedItems)]
	counts := make(map[string]int)
	var partial bool
	for _, item := range listed {
		if item.Category != onepassword.ItemCategorySSHKey || item.State != onepassword.ItemStateActive {
			continue
		}
		if item.VaultID != vaultID || !config.ValidID(item.ID) {
			partial = partial || scope.Includes(vaultID, item.ID)
			continue
		}
		counts[item.ID]++
	}
	var groups [3][]Item
	selected := 0
	for _, item := range listed {
		if item.Category != onepassword.ItemCategorySSHKey || item.State != onepassword.ItemStateActive || item.VaultID != vaultID {
			continue
		}
		included := scope.Includes(vaultID, item.ID)
		if counts[item.ID] != 1 {
			partial = partial || included
			continue
		}
		rank := 2
		if included {
			rank = 0
			selected++
		} else if _, explicit := scope.Items[vaultID+"/"+item.ID]; explicit {
			rank = 1
		}
		if len(groups[rank]) < config.MaxItems {
			groups[rank] = append(groups[rank], Item{ID: item.ID, Name: safeName(item.Title, "SSH key "+item.ID[:8])})
		}
	}
	return groups, selected, partial, overflow
}

func safeName(raw, fallback string) string {
	var out strings.Builder
	for _, r := range raw[:min(len(raw), 4*maxDiscoveryLabel)] {
		if unicode.IsControl(r) || unicode.Is(unicode.Bidi_Control, r) || r == '\u2028' || r == '\u2029' {
			continue
		}
		if out.Len()+utf8.RuneLen(r) > maxDiscoveryLabel {
			break
		}
		out.WriteRune(r)
	}
	if name := strings.TrimSpace(out.String()); name != "" {
		return name
	}
	return fallback
}
