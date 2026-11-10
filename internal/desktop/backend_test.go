package desktop

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"errors"
	"fmt"
	"testing"
	"time"

	onepassword "github.com/1password/onepassword-sdk-go"
	"golang.org/x/crypto/ssh"

	"opssh/internal/config"
)

const vaultID = "aaaaaaaaaaaaaaaaaaaaaaaaaa"
const itemID = "bbbbbbbbbbbbbbbbbbbbbbbbbb"

type fakeResolve func(context.Context, string) (string, error)

func (f fakeResolve) Resolve(ctx context.Context, ref string) (string, error) { return f(ctx, ref) }

type fakeMetadataClient struct {
	fakeResolve
	vaults    []onepassword.VaultOverview
	items     map[string][]onepassword.ItemOverview
	listItems func(string) []onepassword.ItemOverview
}

func (f fakeMetadataClient) Vaults(context.Context) ([]onepassword.VaultOverview, error) {
	return f.vaults, nil
}

func (f fakeMetadataClient) Items(_ context.Context, id string) ([]onepassword.ItemOverview, error) {
	if f.listItems != nil {
		return f.listItems(id), nil
	}
	return f.items[id], nil
}

func TestDiscoveryOnlySurfacesSanitizedActiveSSHMetadata(t *testing.T) {
	const other = "cccccccccccccccccccccccccc"
	client := fakeMetadataClient{
		vaults: []onepassword.VaultOverview{{ID: vaultID, Title: "Test\u202e vault\n", Description: "unused details"}},
		items: map[string][]onepassword.ItemOverview{vaultID: {
			{ID: itemID, VaultID: vaultID, Title: " Test\u2028key ", Category: onepassword.ItemCategorySSHKey, State: onepassword.ItemStateActive},
			{ID: other, VaultID: vaultID, Title: "Archived", Category: onepassword.ItemCategorySSHKey, State: onepassword.ItemStateArchived},
			{ID: other, VaultID: vaultID, Title: "Different category", Category: onepassword.ItemCategoryLogin, State: onepassword.ItemStateActive},
		}},
	}
	b := newBackend("Fixture", func(context.Context, string) (resolver, error) { return client, nil })
	got, err := b.Discover(context.Background(), config.Default().Scope)
	if err != nil || len(got) != 1 || got[0].ID != vaultID || got[0].Name != "Test vault" || len(got[0].Items) != 1 ||
		got[0].Items[0].ID != itemID || got[0].Items[0].Name != "Testkey" {
		t.Fatalf("unexpected metadata: %+v, %v", got, err)
	}
}

func TestDiscoveryRejectsAmbiguousItemReferences(t *testing.T) {
	item := onepassword.ItemOverview{ID: itemID, VaultID: vaultID, Category: onepassword.ItemCategorySSHKey, State: onepassword.ItemStateActive}
	client := fakeMetadataClient{
		vaults: []onepassword.VaultOverview{{ID: vaultID}},
		items:  map[string][]onepassword.ItemOverview{vaultID: {item, item}},
	}
	b := newBackend("Fixture", func(context.Context, string) (resolver, error) { return client, nil })
	got, err := b.Discover(context.Background(), config.Default().Scope)
	if !errors.Is(err, ErrPartial) || len(got) != 1 || len(got[0].Items) != 0 {
		t.Fatalf("ambiguous metadata should be omitted: %+v, %v", got, err)
	}
}

func TestDiscoveryKeepsExplicitSelectionBeyondTheDisplayLimit(t *testing.T) {
	overviews := make([]onepassword.VaultOverview, 70)
	for i := range overviews {
		overviews[i] = onepassword.VaultOverview{ID: fmt.Sprintf("%026d", i)}
	}
	lateVault := overviews[len(overviews)-1].ID
	items := make([]onepassword.ItemOverview, 140)
	for i := range items {
		items[i] = onepassword.ItemOverview{ID: fmt.Sprintf("%026d", i), VaultID: lateVault,
			Category: onepassword.ItemCategorySSHKey, State: onepassword.ItemStateActive}
	}
	lateItem := items[len(items)-1].ID
	client := fakeMetadataClient{vaults: overviews, items: map[string][]onepassword.ItemOverview{lateVault: items}}
	scope := config.Scope{Default: "exclude", Items: map[string]string{lateVault + "/" + lateItem: "include"}}
	b := newBackend("Fixture", func(context.Context, string) (resolver, error) { return client, nil })
	got, err := b.Discover(context.Background(), scope)
	if err != nil || len(got) != config.MaxVaults || got[0].ID != lateVault ||
		len(got[0].Items) != config.MaxItems || got[0].Items[0].ID != lateItem {
		t.Fatalf("display limits hid an explicitly included item: %v", err)
	}
}

func TestDiscoveryReportsAHardScanLimitForAnExcludedAccount(t *testing.T) {
	overviews := make([]onepassword.VaultOverview, 257)
	for i := range overviews {
		overviews[i] = onepassword.VaultOverview{ID: fmt.Sprintf("%026d", i)}
	}
	client := fakeMetadataClient{vaults: overviews}
	b := newBackend("Fixture", func(context.Context, string) (resolver, error) { return client, nil })
	rows, err := b.Discover(context.Background(), config.Scope{Default: "exclude"})
	if !errors.Is(err, ErrScanLimit) || len(rows) != config.MaxVaults {
		t.Fatalf("hard scan limit was not distinguished from the effective selection: %v, %d vaults", err, len(rows))
	}
}

func TestExcludedBrowseKeepsCompletedVaultsAfterCancellation(t *testing.T) {
	const otherVault = "cccccccccccccccccccccccccc"
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	client := fakeMetadataClient{listItems: func(id string) []onepassword.ItemOverview {
		if id == otherVault {
			cancel()
		}
		return []onepassword.ItemOverview{{ID: itemID, VaultID: id, Title: "Example",
			Category: onepassword.ItemCategorySSHKey, State: onepassword.ItemStateActive}}
	}}
	b := newBackend("Fixture", func(context.Context, string) (resolver, error) { return client, nil })
	rows, err := b.Browse(ctx, config.Scope{Default: "exclude"}, []Vault{{ID: vaultID}, {ID: otherVault}})
	if !errors.Is(err, context.Canceled) || len(rows) != 2 || len(rows[0].Items) != 1 ||
		rows[0].Items[0].ID != itemID || len(rows[1].Items) != 0 {
		t.Fatalf("browse lost completed data or returned canceled data: %+v, %v", rows, err)
	}
}

func TestPublicRefreshOnlyResolvesPublicField(t *testing.T) {
	pk, _, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	pub, err := ssh.NewPublicKey(pk)
	if err != nil {
		t.Fatal(err)
	}
	var references []string
	b := newBackend("Test", func(context.Context, string) (resolver, error) {
		return fakeResolve(func(_ context.Context, ref string) (string, error) {
			references = append(references, ref)
			return string(ssh.MarshalAuthorizedKey(pub)), nil
		}), nil
	})
	got, err := b.Public(context.Background(), vaultID, itemID)
	if err != nil || string(got) != string(pub.Marshal()) {
		t.Fatalf("public lookup: %v", err)
	}
	if len(references) != 1 || references[0] != "op://"+vaultID+"/"+itemID+"/public key" {
		t.Fatalf("wrong field requested: %q", references)
	}
}

func TestTimedOutNativeCallRemainsBounded(t *testing.T) {
	started, release := make(chan struct{}), make(chan struct{})
	defer close(release)
	b := newBackend("Test", func(context.Context, string) (resolver, error) {
		return fakeBatchResolver{batch: func([]string) (onepassword.ResolveAllResponse, error) {
			close(started)
			<-release
			return onepassword.ResolveAllResponse{}, nil
		}}, nil
	})
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	done := make(chan error, 1)
	go func() { _, err := b.Private(ctx, vaultID, itemID); done <- err }()
	select {
	case <-started:
	case <-time.After(time.Second):
		t.Fatal("fake backend did not begin")
	}
	cancel()
	select {
	case err := <-done:
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("unexpected failure: %v", err)
		}
	case <-time.After(time.Second):
		t.Fatal("request did not cancel")
	}
	if _, err := b.Private(context.Background(), vaultID, itemID); !errors.Is(err, ErrBusy) {
		t.Fatalf("blocked SDK started extra work: %v", err)
	}
}
