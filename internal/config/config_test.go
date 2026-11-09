package config

import (
	"crypto/ed25519"
	"crypto/rand"
	"crypto/rsa"
	"math/big"
	"os"
	"path/filepath"
	"testing"

	"golang.org/x/crypto/ssh"
)

const vaultID = "aaaaaaaaaaaaaaaaaaaaaaaaaa"
const itemID = "bbbbbbbbbbbbbbbbbbbbbbbbbb"

func TestDefaultsAndPublicIndexRoundTrip(t *testing.T) {
	path := filepath.Join(t.TempDir(), "settings.json")
	s, err := Read(path)
	if err != nil || s.KeyCache.Seconds != 300 || s.ApprovalCache.Seconds != 600 {
		t.Fatalf("defaults: %+v %v", s, err)
	}
	pub, _, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	p, err := ssh.NewPublicKey(pub)
	if err != nil {
		t.Fatal(err)
	}
	s.Account = "Test"
	s.Keys = []Key{{VaultID: vaultID, ItemID: itemID, Name: "Example", Public: p.Marshal()}}
	if err := Save(path, s); err != nil {
		t.Fatal(err)
	}
	got, err := Read(path)
	if err != nil || len(got.Keys) != 1 || string(got.Keys[0].Public) != string(p.Marshal()) {
		t.Fatalf("saved index: %+v %v", got, err)
	}
	s.Keys[0].Public = []byte("not-an-SSH-public-blob")
	if err := Save(path, s); err == nil {
		t.Fatal("invalid key replaced good configuration")
	}
	got, err = Read(path)
	if err != nil || string(got.Keys[0].Public) != string(p.Marshal()) {
		t.Fatal("rejected change corrupted the previous state")
	}
}

func TestRejectsUnexpectedFieldsAndSecretReferenceInjection(t *testing.T) {
	path := filepath.Join(t.TempDir(), "settings.json")
	if err := os.WriteFile(path, []byte(`{"account":"X","privateKey":"not allowed"}`), 0600); err != nil {
		t.Fatal(err)
	}
	if _, err := Read(path); err == nil {
		t.Fatal("unexpected private field accepted")
	}
	s := Default()
	s.Account = "Test"
	s.Keys = []Key{{VaultID: vaultID, ItemID: itemID + "/notes", Name: "X"}}
	if err := Save(path, s); err == nil {
		t.Fatal("invalid item ID accepted")
	}
}

func TestRejectsNegativeRSAModulusBeforePersisting(t *testing.T) {
	n := new(big.Int).Lsh(big.NewInt(-1), 2047)
	public, err := ssh.NewPublicKey(&rsa.PublicKey{N: n, E: 65537})
	if err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(t.TempDir(), "settings.json")
	s := Default()
	s.Account = "Test"
	s.Keys = []Key{{VaultID: vaultID, ItemID: itemID, Public: public.Marshal()}}
	if err := Save(path, s); err == nil {
		t.Fatal("invalid RSA modulus persisted")
	}
	if _, err := os.Stat(path); !os.IsNotExist(err) {
		t.Fatalf("invalid configuration created a file: %v", err)
	}
}
