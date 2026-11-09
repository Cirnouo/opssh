// Package config owns only public configuration and selected key metadata.
package config

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"os"
	"path/filepath"
	"strings"
	"unicode"

	"opssh/internal/engine"
)

const maxFile = 256 * 1024
const MaxKeys = 64

var ErrInvalid = errors.New("invalid public configuration")

type Policy struct {
	Mode    string `json:"mode"`
	Seconds int    `json:"seconds"`
}

type Key struct {
	VaultID string `json:"vaultId"`
	ItemID  string `json:"itemId"`
	Name    string `json:"name"`
	Public  []byte `json:"public,omitempty"`
}

func (k Key) ID() string { return k.VaultID + "/" + k.ItemID }

type Settings struct {
	Account       string `json:"account"`
	KeyCache      Policy `json:"keyCache"`
	ApprovalCache Policy `json:"approvalCache"`
	Keys          []Key  `json:"keys"`
}

func Default() Settings {
	return Settings{
		KeyCache:      Policy{Mode: "fixed", Seconds: 300},
		ApprovalCache: Policy{Mode: "fixed", Seconds: 600},
		Keys:          []Key{},
	}
}

func Path() (string, error) {
	root := os.Getenv("LOCALAPPDATA")
	if !filepath.IsAbs(root) {
		return "", errors.New("LocalAppData is unavailable")
	}
	return filepath.Join(root, "opssh", "settings.json"), nil
}

func ValidID(value string) bool {
	if len(value) != 26 {
		return false
	}
	for _, ch := range value {
		if !(ch >= 'a' && ch <= 'z') && !(ch >= '0' && ch <= '9') {
			return false
		}
	}
	return true
}

func ValidLabel(value string) bool {
	return len(value) <= 256 && strings.TrimSpace(value) == value && strings.IndexFunc(value, unicode.IsControl) == -1
}

func (p Policy) Valid() bool {
	switch p.Mode {
	case "off", "until-lock":
		return p.Seconds == 0
	case "fixed", "idle":
		return p.Seconds > 0 && p.Seconds <= 30*24*60*60
	default:
		return false
	}
}

func (s Settings) Validate() error {
	if !ValidLabel(s.Account) || !s.KeyCache.Valid() || !s.ApprovalCache.Valid() ||
		len(s.Keys) > MaxKeys || (s.Account == "" && len(s.Keys) > 0) {
		return ErrInvalid
	}
	ids := make(map[string]bool, len(s.Keys))
	blobs := make(map[string]bool, len(s.Keys))
	for _, k := range s.Keys {
		if !ValidID(k.VaultID) || !ValidID(k.ItemID) || !ValidLabel(k.Name) || ids[k.ID()] {
			return ErrInvalid
		}
		ids[k.ID()] = true
		if len(k.Public) > 0 {
			if !engine.ValidPublic(k.Public) || blobs[string(k.Public)] {
				return ErrInvalid
			}
			blobs[string(k.Public)] = true
		}
	}
	return nil
}

func Read(path string) (Settings, error) {
	f, err := os.Open(path)
	if errors.Is(err, os.ErrNotExist) {
		return Default(), nil
	}
	if err != nil {
		return Settings{}, err
	}
	defer f.Close()
	data, err := io.ReadAll(io.LimitReader(f, maxFile+1))
	if err != nil {
		return Settings{}, err
	}
	if len(data) > maxFile || !json.Valid(data) || bytes.TrimSpace(data)[0] != '{' {
		return Settings{}, ErrInvalid
	}
	s := Default()
	d := json.NewDecoder(bytes.NewReader(data))
	d.DisallowUnknownFields()
	if err := d.Decode(&s); err != nil {
		return Settings{}, ErrInvalid
	}
	return s, s.Validate()
}

func Save(path string, s Settings) error {
	if err := s.Validate(); err != nil {
		return err
	}
	data, err := json.MarshalIndent(s, "", "  ")
	if err != nil {
		return err
	}
	dir := filepath.Dir(path)
	if err := os.MkdirAll(dir, 0700); err != nil {
		return err
	}
	f, err := os.CreateTemp(dir, ".settings-*.tmp")
	if err != nil {
		return err
	}
	defer os.Remove(f.Name())
	defer f.Close()
	if _, err := f.Write(append(data, '\n')); err != nil {
		return err
	}
	if err := f.Sync(); err != nil {
		return err
	}
	if err := f.Close(); err != nil {
		return err
	}
	return os.Rename(f.Name(), path)
}
