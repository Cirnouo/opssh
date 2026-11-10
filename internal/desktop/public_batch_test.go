package desktop

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"fmt"
	"strings"
	"testing"

	onepassword "github.com/1password/onepassword-sdk-go"
	"golang.org/x/crypto/ssh"

	"opssh/internal/config"
)

type fakeBatchResolver struct {
	fakeResolve
	batch func([]string) (onepassword.ResolveAllResponse, error)
}

func (f fakeBatchResolver) ResolveAll(_ context.Context, refs []string) (onepassword.ResolveAllResponse, error) {
	return f.batch(refs)
}

func TestPublicBatchReadsOnlyBoundedPublicReferencesAndChecksIdentity(t *testing.T) {
	public, err := ssh.NewPublicKey(ed25519.PublicKey(make([]byte, ed25519.PublicKeySize)))
	if err != nil {
		t.Fatal(err)
	}
	keys := make([]config.Key, 33)
	for i := range keys {
		keys[i] = config.Key{VaultID: vaultID, ItemID: fmt.Sprintf("%026d", i)}
	}
	calls := 0
	client := fakeBatchResolver{
		fakeResolve: func(context.Context, string) (string, error) {
			t.Error("unexpected individual lookup")
			return "", ErrUnavailable
		},
		batch: func(refs []string) (onepassword.ResolveAllResponse, error) {
			size := 32
			if calls > 0 {
				size = 1
			}
			if len(refs) != size {
				t.Errorf("unexpected batch length %d, want %d", len(refs), size)
			}
			response := onepassword.ResolveAllResponse{
				IndividualResponses: make(map[string]onepassword.Response[onepassword.ResolvedReference, onepassword.ResolveReferenceError]),
			}
			for i, ref := range refs {
				key := keys[calls*32+i]
				want := "op://" + key.VaultID + "/" + key.ItemID + "/public key"
				if ref != want {
					t.Errorf("unexpected reference %q, want %q", ref, want)
				}
				content := &onepassword.ResolvedReference{
					VaultID: key.VaultID,
					ItemID:  key.ItemID,
					Secret:  strings.TrimSpace(string(ssh.MarshalAuthorizedKey(public))),
				}
				switch calls*32 + i {
				case 0:
					content.ItemID = itemID
				case 1:
					content.Secret = "not an SSH public key"
				case 2:
					continue
				}
				response.IndividualResponses[ref] = onepassword.Response[onepassword.ResolvedReference, onepassword.ResolveReferenceError]{Content: content}
			}
			calls++
			return response, nil
		},
	}
	backend := newBackend("Fixture", func(context.Context, string) (resolver, error) { return client, nil })
	got, err := backend.PublicBatch(context.Background(), keys)
	if err != nil || len(got) != len(keys) || calls != 2 {
		t.Fatalf("batch shape: calls=%d, results=%d, err=%v", calls, len(got), err)
	}
	for i, blob := range got {
		if (i < 3 && len(blob) != 0) || (i >= 3 && !bytes.Equal(blob, public.Marshal())) {
			t.Errorf("unexpected public value at %d", i)
		}
	}
}
