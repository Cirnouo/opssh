# Development and contributions

Read [AGENTS.md](AGENTS.md) and the relevant [behavior specification](docs/specs/windows-agent.md). Use the Go and .NET versions pinned in `go.mod` and `global.json`, PowerShell 7, and Windows x64 system OpenSSH.

Use a focused conventional topic branch and an isolated worktree. In a new worktree, enable the repository's hooks for that worktree:

```powershell
git config --local extensions.worktreeConfig true
git config --worktree core.hooksPath .githooks
```

Run `./tools/Test-Windows.ps1` for Go tests, Go vet, locked .NET restore, tray tests, Go/tray process interop, and the actual Windows Release folder. The integration harness uses a separate build-tagged core that generates its own test key in memory. It interacts with the local SSH agent protocol and the test tray control channel. See the [verification policy](docs/policies/verification.md) for the commit gate and evidence boundaries.

Stage only the intended files, check the diff, and use an ordinary commit. The pre-commit hook archives the exact staged tree and runs the Windows driver in that snapshot. The commit-msg hook consumes the successful receipt only for the same tree. GitHub Actions validates PRs and main independently. For the live provider path, follow the [separate procedure](docs/references/1password-desktop-validation.md).
