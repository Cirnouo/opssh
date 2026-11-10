package desktop

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"errors"
	"fmt"
	"io/fs"
	"strings"
	"testing"

	onepassword "github.com/1password/onepassword-sdk-go"
	"golang.org/x/crypto/ssh"

	"opssh/internal/config"
)

func TestPrivateLookupSeparatesPublicIndexFromPrivateFailures(t *testing.T) {
	keys := []config.Key{
		{VaultID: vaultID, ItemID: itemID},
		{VaultID: vaultID, ItemID: "cccccccccccccccccccccccccc"},
		{VaultID: vaultID, ItemID: "dddddddddddddddddddddddddd"},
	}
	blobs := make([][]byte, len(keys))
	public := make(map[string]onepassword.Response[onepassword.ResolvedReference, onepassword.ResolveReferenceError])
	for i, key := range keys {
		pk, _, err := ed25519.GenerateKey(rand.Reader)
		if err != nil {
			t.Fatal(err)
		}
		pub, err := ssh.NewPublicKey(pk)
		if err != nil {
			t.Fatal(err)
		}
		blobs[i] = pub.Marshal()
		public["op://"+key.ID()+"/public key"] = onepassword.Response[onepassword.ResolvedReference, onepassword.ResolveReferenceError]{
			Content: &onepassword.ResolvedReference{VaultID: key.VaultID, ItemID: key.ItemID, Secret: string(ssh.MarshalAuthorizedKey(pub))},
		}
	}
	missing := onepassword.NewResolveReferenceErrorTypeVariantFieldNotFound()
	invalid := onepassword.NewResolveReferenceErrorTypeVariantUnableToFormatPrivateKeyToOpenSSH()
	unknown := errors.New("synthetic provider details")
	observed := 0
	client := fakeBatchResolver{
		fakeResolve: func(context.Context, string) (string, error) { return "", unknown },
		batch: func(refs []string) (onepassword.ResolveAllResponse, error) {
			if len(refs) == 3 {
				for _, ref := range refs {
					if _, ok := public[ref]; !ok {
						t.Error("public indexing requested a different field")
					}
				}
				return onepassword.ResolveAllResponse{IndividualResponses: public}, nil
			}
			if len(refs) != 1 || observed >= len(keys) || refs[0] != "op://"+keys[observed].ID()+"/private_key?ssh-format=openssh" {
				t.Error("private lookup did not select the stable private field ID")
				return onepassword.ResolveAllResponse{}, unknown
			}
			observed++
			response := onepassword.Response[onepassword.ResolvedReference, onepassword.ResolveReferenceError]{}
			switch observed {
			case 1:
				response.Error = &missing
			case 2:
				response.Error = &invalid
			default:
				return onepassword.ResolveAllResponse{}, unknown
			}
			return onepassword.ResolveAllResponse{IndividualResponses: map[string]onepassword.Response[onepassword.ResolvedReference, onepassword.ResolveReferenceError]{refs[0]: response}}, nil
		},
	}
	b := newBackend("Fixture", func(context.Context, string) (resolver, error) { return client, nil })
	indexed, err := b.PublicBatch(t.Context(), keys)
	if err != nil || len(indexed) != len(keys) || observed != 0 {
		t.Fatalf("public indexing failed: %d keys, %v", len(indexed), err)
	}
	for i, key := range keys {
		if !bytes.Equal(indexed[i], blobs[i]) {
			t.Fatalf("public index lost key %d", i)
		}
		got, err := b.Private(t.Context(), key.VaultID, key.ItemID)
		want := []string{"private key field is missing", "private key field is invalid", "desktop request failed"}[i]
		if len(got) != 0 || err == nil || err.Error() != want {
			t.Errorf("private failure %d: got %v, want %q", i, err, want)
		}
		if i < 2 && b.Status() != "last-success" {
			t.Errorf("private field failure changed the successful authorization observation to %q", b.Status())
		}
	}
}

func TestPrivateLookupChecksFieldSizeAndReturnedIdentity(t *testing.T) {
	for _, tc := range []struct {
		name   string
		item   string
		secret string
		want   string
	}{
		{"exact", itemID, "synthetic private response", ""},
		{"other item", "cccccccccccccccccccccccccc", "synthetic private response", "desktop request failed"},
		{"empty", itemID, "", "private key field is empty"},
		{"oversized", itemID, strings.Repeat("x", 64*1024+1), "private key field is invalid"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			const ref = "op://" + vaultID + "/" + itemID + "/private_key?ssh-format=openssh"
			client := fakeBatchResolver{
				fakeResolve: func(context.Context, string) (string, error) { return tc.secret, nil },
				batch: func(refs []string) (onepassword.ResolveAllResponse, error) {
					if len(refs) != 1 || refs[0] != ref {
						t.Error("unexpected private field reference")
						return onepassword.ResolveAllResponse{}, ErrUnavailable
					}
					content := &onepassword.ResolvedReference{VaultID: vaultID, ItemID: tc.item, Secret: tc.secret}
					return onepassword.ResolveAllResponse{IndividualResponses: map[string]onepassword.Response[onepassword.ResolvedReference, onepassword.ResolveReferenceError]{ref: {Content: content}}}, nil
				},
			}
			b := newBackend("Fixture", func(context.Context, string) (resolver, error) { return client, nil })
			got, err := b.Private(t.Context(), vaultID, itemID)
			defer clear(got)
			if tc.want == "" {
				if err != nil || string(got) != tc.secret {
					t.Fatal("private lookup rejected an exact response")
				}
				return
			}
			if len(got) != 0 || err == nil || err.Error() != tc.want {
				t.Errorf("got %v, want %q", err, tc.want)
			}
		})
	}
}

func TestPrivateLookupKeepsAuthorizationErrorsDistinctFromOpaqueErrors(t *testing.T) {
	for _, tc := range []struct {
		name   string
		source error
		want   string
		status string
	}{
		{"expired", &onepassword.DesktopSessionExpiredError{}, "desktop authorization expired", "expired"},
		{"denied", fmt.Errorf("native boundary: %w", fs.ErrPermission), "desktop access denied", "denied"},
		{"opaque", errors.New("synthetic refusal with provider details"), "desktop request failed", "unavailable"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			client := fakeBatchResolver{
				fakeResolve: func(context.Context, string) (string, error) { return "", tc.source },
				batch: func([]string) (onepassword.ResolveAllResponse, error) {
					return onepassword.ResolveAllResponse{}, tc.source
				},
			}
			b := newBackend("Fixture", func(context.Context, string) (resolver, error) { return client, nil })
			got, err := b.Private(t.Context(), vaultID, itemID)
			if len(got) != 0 || err == nil || err.Error() != tc.want || b.Status() != tc.status {
				t.Errorf("got %v with status %q, want %q with status %q", err, b.Status(), tc.want, tc.status)
			}
		})
	}
}
