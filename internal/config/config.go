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

const CurrentVersion = 2

const (
	MaxKeys   = 64
	MaxVaults = 64
	MaxItems  = 128
)

const (
	IndexNotIndexed    = "not-indexed"
	IndexReady         = "ready"
	IndexPartial       = "partial"
	IndexUnavailable   = "unavailable"
	IndexScopeTooBroad = "scope-too-broad"
)

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

type Scope struct {
	Default string            `json:"default"`
	Vaults  map[string]string `json:"vaults"`
	Items   map[string]string `json:"items"`
}

func (s Scope) Normalized() Scope {
	if s.Vaults == nil {
		s.Vaults = make(map[string]string)
	}
	if s.Items == nil {
		s.Items = make(map[string]string)
	}
	return s
}

func (s Scope) Includes(vaultID, itemID string) bool {
	if rule, ok := s.Items[vaultID+"/"+itemID]; ok {
		return rule == "include"
	}
	return s.IncludesVault(vaultID)
}

func (s Scope) IncludesVault(vaultID string) bool {
	if rule, ok := s.Vaults[vaultID]; ok {
		return rule == "include"
	}
	return s.Default == "include"
}

func (s Scope) Valid() bool {
	validRule := func(rule string) bool { return rule == "include" || rule == "exclude" }
	if !validRule(s.Default) || len(s.Vaults) > MaxVaults || len(s.Items) > MaxItems {
		return false
	}
	for id, rule := range s.Vaults {
		if !ValidID(id) || !validRule(rule) {
			return false
		}
	}
	for id, rule := range s.Items {
		vault, item, ok := strings.Cut(id, "/")
		if !ok || !ValidID(vault) || !ValidID(item) || !validRule(rule) {
			return false
		}
	}
	return true
}

type Vault struct {
	ID   string `json:"id"`
	Name string `json:"name"`
}

type Settings struct {
	Version       int     `json:"version"`
	AccountName   string  `json:"accountName,omitempty"`
	Vaults        []Vault `json:"vaults,omitempty"`
	Account       string  `json:"account"`
	KeyCache      Policy  `json:"keyCache"`
	ApprovalCache Policy  `json:"approvalCache"`
	Keys          []Key   `json:"keys"`
	Scope         Scope   `json:"scope"`
	Index         string  `json:"index"`
}

func Default() Settings {
	return Settings{
		Version:       CurrentVersion,
		KeyCache:      Policy{Mode: "fixed", Seconds: 300},
		ApprovalCache: Policy{Mode: "fixed", Seconds: 600},
		Keys:          []Key{},
		Scope:         Scope{Default: "include"}.Normalized(),
		Index:         IndexNotIndexed,
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
	case "off", "until-exit":
		return p.Seconds == 0
	case "fixed", "idle":
		return p.Seconds > 0 && p.Seconds <= 30*24*60*60
	default:
		return false
	}
}

func (s Settings) Validate() error {
	if s.Version != CurrentVersion || !ValidLabel(s.AccountName) || len(s.Vaults) > MaxVaults ||
		(s.Account == "" && (s.AccountName != "" || len(s.Vaults) != 0)) ||
		!ValidLabel(s.Account) || !s.KeyCache.Valid() || !s.ApprovalCache.Valid() ||
		len(s.Keys) > MaxKeys || (s.Account == "" && len(s.Keys) > 0) || !s.Scope.Valid() {
		return ErrInvalid
	}
	switch s.Index {
	case IndexNotIndexed, IndexReady, IndexPartial, IndexUnavailable, IndexScopeTooBroad:
	default:
		return ErrInvalid
	}
	vaultIDs := make(map[string]bool, len(s.Vaults))
	for _, v := range s.Vaults {
		if !ValidID(v.ID) || !ValidLabel(v.Name) || v.Name == "" || vaultIDs[v.ID] {
			return ErrInvalid
		}
		vaultIDs[v.ID] = true
	}
	ids := make(map[string]bool, len(s.Keys))
	blobs := make(map[string]bool, len(s.Keys))
	for _, k := range s.Keys {
		if !ValidID(k.VaultID) || !ValidID(k.ItemID) || !ValidLabel(k.Name) || ids[k.ID()] ||
			!s.Scope.Includes(k.VaultID, k.ItemID) {
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
	s.Version = 0
	s.Index = ""
	wire := struct {
		*Settings
		Scope json.RawMessage `json:"scope"`
	}{Settings: &s}
	d := json.NewDecoder(bytes.NewReader(data))
	d.DisallowUnknownFields()
	if err := d.Decode(&wire); err != nil {
		return Settings{}, ErrInvalid
	}
	if len(wire.Scope) > 0 {
		s.Scope = Scope{}
		d := json.NewDecoder(bytes.NewReader(wire.Scope))
		d.DisallowUnknownFields()
		if err := d.Decode(&s.Scope); err != nil {
			return Settings{}, ErrInvalid
		}
		s.Scope = s.Scope.Normalized()
	}
	if s.Version == 0 {
		migrateUnversioned(&s, len(wire.Scope) != 0)
	}
	if s.Version == 1 {
		defaults := Default()
		if s.KeyCache == (Policy{Mode: "until-lock"}) {
			s.KeyCache = defaults.KeyCache
		}
		if s.ApprovalCache == (Policy{Mode: "until-lock"}) {
			s.ApprovalCache = defaults.ApprovalCache
		}
		s.Version = 2
	}
	if s.Keys == nil {
		s.Keys = []Key{}
	}
	return s, s.Validate()
}

func migrateUnversioned(s *Settings, hasScope bool) {
	if !hasScope && s.Account != "" {
		s.Scope.Default = "exclude"
		for _, k := range s.Keys {
			s.Scope.Items[k.ID()] = "include"
		}
	}
	if s.Index == "" {
		s.Index = IndexNotIndexed
		if len(s.Keys) > 0 {
			s.Index = IndexReady
			for _, k := range s.Keys {
				if len(k.Public) == 0 {
					s.Index = IndexPartial
					break
				}
			}
		}
	}
	s.Version = 1
}

func Save(path string, s Settings) error {
	if err := s.Validate(); err != nil {
		return err
	}
	s.Scope = s.Scope.Normalized()
	data, err := json.MarshalIndent(s, "", "  ")
	if err != nil {
		return err
	}
	if len(data) >= maxFile {
		return ErrInvalid
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
