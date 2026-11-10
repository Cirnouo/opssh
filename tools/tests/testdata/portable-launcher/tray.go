package main

import (
	"os"
	"path/filepath"
	"slices"
	"strings"
)

func main() {
	exe, exeErr := os.Executable()
	cwd, cwdErr := os.Getwd()
	want := []string{"a path with spaces", "embedded \"quotes\"", "trailing\\"}
	if exeErr != nil || cwdErr != nil || !strings.EqualFold(cwd, filepath.Dir(exe)) || !slices.Equal(os.Args[1:], want) {
		os.Exit(1)
	}
	if err := os.WriteFile("portable-launch.pending", []byte("launched"), 0600); err != nil {
		os.Exit(1)
	}
	if err := os.Rename("portable-launch.pending", "portable-launch.receipt"); err != nil {
		os.Exit(1)
	}
}
