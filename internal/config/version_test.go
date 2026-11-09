package config

import (
	"errors"
	"os"
	"path/filepath"
	"testing"
)

func TestVersionedSettingsMigrateUnversionedFile(t *testing.T) {
	path := filepath.Join(t.TempDir(), "settings.json")
	old := []byte(`{"account":"Existing","scope":{"default":"exclude"},"keyCache":{"mode":"idle","seconds":60}}`)
	if err := os.WriteFile(path, old, 0600); err != nil {
		t.Fatal(err)
	}
	settings, err := Read(path)
	if err != nil || settings.Version != CurrentVersion || settings.Account != "Existing" ||
		settings.Scope.Default != "exclude" || settings.KeyCache != (Policy{Mode: "idle", Seconds: 60}) {
		t.Fatalf("migration lost settings: %+v %v", settings, err)
	}
	if err := Save(path, settings); err != nil {
		t.Fatal(err)
	}
	if got, err := Read(path); err != nil || got.Version != CurrentVersion {
		t.Fatalf("version was not saved: %+v %v", got, err)
	}
}

func TestReadRejectsFutureSettingsVersion(t *testing.T) {
	path := filepath.Join(t.TempDir(), "settings.json")
	if err := os.WriteFile(path, []byte(`{"version":1000}`), 0600); err != nil {
		t.Fatal(err)
	}
	if _, err := Read(path); !errors.Is(err, ErrInvalid) {
		t.Fatalf("future version accepted: %v", err)
	}
}
