# Contributor instructions

Inspect the working tree before editing and preserve unrelated work. Apply ancestor and local instructions together. Use UTF-8 and follow surrounding style. Use one focused conventional lowercase topic branch, managed worktree and PR into `main` per change. Make atomic Conventional Commits and squash the PR. Publishing a release or changing remote rules requires authorization for that action.

The Go core owns Windows peer identity, approvals, key material, signing and the persistent public index. The .NET WinUI 3 tray owns interaction and supervises the core. Preserve the private control channel and its process and request binding. Keep provider permission, loaded keys and process approvals independent. Bound external input and concurrency, propagate cancellation, discard stale results and dispose resources on success and failure. Keep private keys, credentials, personal paths and raw secret-bearing errors out of logs, configuration, fixtures, diagnostics and public evidence.

Ordinary verification is offline. Use generated in-memory keys, controlled clocks and fake providers. Validate the final staged tree and the Windows Release distributable built from it. Report the file state, commands and platform paths exercised. Keep local checks, CI and live provider evidence separate. Live 1Password testing requires separately approved steps and a dedicated test item.

## Read before the relevant action

| Before | Canonical owner |
| --- | --- |
| Changing Go or WinUI implementation, IPC, settings or lifecycle | [Maintenance policy](docs/policies/maintenance.md), the [current behavior specification](docs/specs/windows-agent.md) and its linked decisions. |
| Designing or implementing Windows UI or install, upgrade or uninstall flows | [Windows design workflow](docs/policies/repository-governance.md#windows-design-workflow). |
| Choosing checks, committing or reporting verification | [Verification policy](docs/policies/verification.md). |
| Testing with a live 1Password account or publishing provider evidence | [Desktop validation procedure](docs/references/1password-desktop-validation.md) and the [documentation policy](docs/policies/documentation.md). |
| Editing READMEs, specs, references, evidence, guides or the changelog | [Documentation policy](docs/policies/documentation.md). |
| Editing contributor instructions or their references | [Instruction progressive disclosure](docs/policies/documentation.md#instruction-progressive-disclosure). |
| Creating a branch or worktree, opening or merging a PR, or changing CI or rules | [Repository governance](docs/policies/repository-governance.md) and the [PR template](.github/PULL_REQUEST_TEMPLATE.md) for PRs. |
| Preparing a stable tag or publishing a Windows release | [Stable release policy](docs/policies/releases.md). |

Use `gpt-6.1-sol` with ultra reasoning for decision and review subagents. Do not use `gpt-6-astra`. Before pushing, a `gpt-6-luna` high subagent runs and follows local checks and reports their outcomes. A `gpt-6-luna` medium subagent follows PR CI and CI for the exact integrated `main` commit. Fast-forward local `main` after CI and retain the managed worktree that is the current chat checkout.

For human setup, see [CONTRIBUTING.md](CONTRIBUTING.md).
