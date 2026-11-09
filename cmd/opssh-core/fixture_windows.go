//go:build opssh_fixture

// This executable is built into an isolated offline test directory only.
package main

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"os"
	"path/filepath"

	"golang.org/x/crypto/ssh"

	"opssh/internal/config"
	"opssh/internal/winipc"
)

type fixtureVault struct {
	public, private []byte
}

func (v *fixtureVault) Public(context.Context, string, string) ([]byte, error) {
	return bytes.Clone(v.public), nil
}
func (v *fixtureVault) Private(context.Context, string, string) ([]byte, error) {
	return bytes.Clone(v.private), nil
}
func (*fixtureVault) Status() string { return "offline-fixture" }

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
	cfg.Account = "Offline fixture"
	cfg.Keys = []config.Key{{VaultID: "aaaaaaaaaaaaaaaaaaaaaaaaaa", ItemID: "bbbbbbbbbbbbbbbbbbbbbbbbbb",
		Name: "Generated offline key", Public: store.public}}
	boot := startup{
		readConfig: func() (string, config.Settings, error) { return filepath.Join(dir, "fixture-settings.json"), cfg, nil },
		newVault:   func(string) vault { return store },
		listen: func() (*winipc.Listener, error) {
			var nonce [8]byte
			if _, err := rand.Read(nonce[:]); err != nil {
				return nil, err
			}
			l, err := winipc.ListenNamed(`\\.\pipe\opssh-fixture-` + hex.EncodeToString(nonce[:]))
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
