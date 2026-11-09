//go:build !opssh_fixture

package main

import (
	"os"

	"opssh/internal/config"
	"opssh/internal/desktop"
	"opssh/internal/winipc"
)

func main() {
	boot := startup{
		readConfig: func() (string, config.Settings, error) {
			path, err := config.Path()
			if err != nil {
				return "", config.Settings{}, err
			}
			cfg, err := config.Read(path)
			return path, cfg, err
		},
		listen:   winipc.Listen,
		newVault: func(account string) vault { return desktop.New(account) },
	}
	if run(os.Args[1:], boot) != nil {
		os.Exit(1)
	}
}
