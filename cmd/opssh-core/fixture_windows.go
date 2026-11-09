//go:build opssh_fixture

// This executable is built into an isolated offline test directory only.
package main

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"encoding/json"
	"encoding/pem"
	"fmt"
	"os"
	"path/filepath"

	"golang.org/x/crypto/ssh"

	"opssh/internal/config"
	"opssh/internal/desktop"
	"opssh/internal/winipc"
)

const (
	fixtureVaultID = "aaaaaaaaaaaaaaaaaaaaaaaaaa"
	fixtureItemID  = "bbbbbbbbbbbbbbbbbbbbbbbbbb"
	fixtureKeyName = "Generated offline key"
)

type fixtureVault struct {
	public, private []byte
}

func (v *fixtureVault) Public(_ context.Context, vault, item string) ([]byte, error) {
	if vault != fixtureVaultID || item != fixtureItemID {
		return nil, desktop.ErrUnavailable
	}
	return bytes.Clone(v.public), nil
}
func (v *fixtureVault) Private(_ context.Context, vault, item string) ([]byte, error) {
	if vault != fixtureVaultID || item != fixtureItemID {
		return nil, desktop.ErrUnavailable
	}
	return bytes.Clone(v.private), nil
}
func (*fixtureVault) Status() string { return "offline-fixture" }

func (*fixtureVault) ListVaults(context.Context) ([]desktop.Vault, error) {
	return []desktop.Vault{{ID: fixtureVaultID, Name: "Offline test vault", Items: []desktop.Item{}}}, nil
}

func (*fixtureVault) Discover(context.Context, config.Scope) ([]desktop.Vault, error) {
	return []desktop.Vault{{ID: fixtureVaultID, Name: "Offline test vault",
		Items: []desktop.Item{{ID: fixtureItemID, Name: fixtureKeyName}}}}, nil
}

func main() {
	if !runFixture() {
		os.Exit(1)
	}
}

func runFixture() bool {
	dir := os.Getenv("OPSSH_FIXTURE_DIRECTORY")
	if !filepath.IsAbs(dir) {
		return false
	}
	info, err := os.Stat(dir)
	if err != nil || !info.IsDir() {
		return false
	}
	pub, private, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		return false
	}
	defer clear(private)
	public, err := ssh.NewPublicKey(pub)
	if err != nil {
		return false
	}
	block, err := ssh.MarshalPrivateKey(private, "")
	if err != nil {
		return false
	}
	store := &fixtureVault{public: public.Marshal(), private: pem.EncodeToMemory(block)}
	clear(block.Bytes)
	defer clear(store.private)
	cfg := config.Default()
	if os.Getenv("OPSSH_FIXTURE_UNCONFIGURED") != "1" {
		cfg.Account = "Offline fixture"
		cfg.Keys = []config.Key{{VaultID: fixtureVaultID, ItemID: fixtureItemID, Name: fixtureKeyName, Public: store.public}}
		cfg.Index = config.IndexReady
	}
	path := filepath.Join(dir, "fixture-settings.json")
	if _, err := os.Stat(path); os.IsNotExist(err) {
		if err := config.Save(path, cfg); err != nil {
			return false
		}
	}
	digest := sha256.Sum256([]byte(dir))
	endpoint := fmt.Sprintf(`\\.\pipe\opssh-fixture-%x`, digest[:8])
	boot := startup{
		readConfig: func() (string, config.Settings, error) {
			saved, err := config.Read(path)
			return path, saved, err
		},
		endpoint: func() (string, error) { return endpoint, nil },
		newVault: func(string) vault { return store },
		listen: func() (*winipc.Listener, error) {
			l, err := winipc.ListenNamed(endpoint)
			if err != nil {
				return nil, err
			}
			publicPath := filepath.Join(dir, "fixture.pub")
			if err := os.WriteFile(publicPath, ssh.MarshalAuthorizedKey(public), 0600); err != nil {
				l.Close()
				return nil, err
			}
			data, err := json.Marshal(struct {
				Pipe       string `json:"pipe"`
				PID        int    `json:"pid"`
				PublicPath string `json:"publicPath"`
			}{l.Addr(), os.Getpid(), publicPath})
			if err == nil {
				err = os.WriteFile(filepath.Join(dir, "fixture.json"), data, 0600)
			}
			if err != nil {
				l.Close()
				return nil, err
			}
			return l, nil
		},
	}
	return run(os.Args[1:], boot) == nil
}
