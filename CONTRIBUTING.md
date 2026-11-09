# Development and contributions

Read [AGENTS.md](AGENTS.md) for repository boundaries, the [Windows agent specification](docs/specs/windows-agent.md) for current behavior and the [maintenance policy](docs/policies/maintenance.md) before changing Go or WinUI code. For installation and usage, see the [English overview](README.md) or the [Simplified Chinese overview](README.zh-Hans.md).

## Prepare a worktree

Use the Go and .NET versions pinned in [go.mod](go.mod) and [global.json](global.json), PowerShell 7 and Windows x64 system OpenSSH. Create a focused topic branch in an isolated worktree using the [repository workflow](docs/policies/repository-governance.md#branch-and-pull-request-lifecycle). Enable the repository hooks in the worktree where you will commit.

```powershell
git config --local extensions.worktreeConfig true
git config --worktree core.hooksPath .githooks
git config --worktree --get core.hooksPath
```

## Verify and submit

Run [`./tools/Test-Windows.ps1`](tools/Test-Windows.ps1) to exercise the offline Windows checks and build a fresh Release folder. Follow the [verification policy](docs/policies/verification.md) for coverage, the staged-tree gate and evidence. For authorized live provider work, use the [separate 1Password procedure](docs/references/1password-desktop-validation.md).

Stage only the intended files, inspect the diff and use an ordinary commit. Open a focused PR into `main` with the [PR template](.github/PULL_REQUEST_TEMPLATE.md) and follow [repository governance](docs/policies/repository-governance.md) through squash and independent CI. Update affected specifications and user guidance under the [documentation policy](docs/policies/documentation.md).
