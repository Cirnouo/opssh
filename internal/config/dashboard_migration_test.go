package config

import (
	"errors"
	"os"
	"path/filepath"
	"testing"
)

func TestDashboardSettingsMigrateRemovedCacheMode(t *testing.T) {
	path := filepath.Join(t.TempDir(), "settings.json")
	legacy := []byte(`{"version":1,"account":"Fixture","index":"not-indexed","scope":{"default":"exclude"},"keyCache":{"mode":"until-lock","seconds":0},"approvalCache":{"mode":"idle","seconds":120}}`)
	if err := os.WriteFile(path, legacy, 0600); err != nil {
		t.Fatal(err)
	}
	got, err := Read(path)
	if err != nil {
		t.Fatal(err)
	}
	if got.Version != CurrentVersion || got.Account != "Fixture" || got.Scope.Default != "exclude" ||
		got.KeyCache != Default().KeyCache || got.ApprovalCache != (Policy{Mode: "idle", Seconds: 120}) {
		t.Fatalf("unexpected dashboard migration: %+v", got)
	}
}

func TestDashboardSettingsRejectMalformedLegacyCache(t *testing.T) {
	path := filepath.Join(t.TempDir(), "settings.json")
	legacy := []byte(`{"version":1,"index":"not-indexed","keyCache":{"mode":"off","seconds":1}}`)
	if err := os.WriteFile(path, legacy, 0600); err != nil {
		t.Fatal(err)
	}
	if _, err := Read(path); !errors.Is(err, ErrInvalid) {
		t.Fatalf("malformed legacy cache accepted: %v", err)
	}
}
