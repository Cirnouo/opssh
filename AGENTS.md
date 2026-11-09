# Contributor instructions

Inspect the working tree before editing and preserve unrelated work. Apply ancestor and local instructions together. Use UTF-8, follow surrounding style, and keep each change focused. Use conventional lowercase branch names such as `feat/windows-agent` and Conventional Commit subjects. Use a focused PR into `main` with squash history. Publishing a release or changing remote rules requires authorization for that action.

The Go core owns Windows peer identity, approvals, key material, signing and its persistent public index. The .NET WinUI 3 tray owns interaction and supervises the core. Preserve the private control channel and its process and request binding. Keep provider permission, loaded keys and process approvals independent. Bound external input and concurrency, propagate cancellation, retire stale results, and dispose resources on success and failure. Never put private keys, credentials, personal paths or raw secret-bearing errors in logs, configuration, fixtures, diagnostics or public evidence.

Ordinary verification is offline: use generated in-memory keys, controlled clocks and fake provider boundaries. Validate the final change, report precisely which files, commands and platform paths were exercised, and distinguish local checks from CI and live provider tests. Test the distributable built from the intended sources. Live 1Password testing requires separately approved steps and a dedicated test item.

## Read before the relevant action

| Before | Canonical owner |
| --- | --- |
| Changing implementation, IPC, settings or lifecycle | The applicable current specification under `docs/specs/` and linked decision records. |
| Choosing tests, committing or reporting verification | [Verification policy](docs/policies/verification.md). |
| Editing instructions, specs, evidence or guides | [Documentation policy](docs/policies/documentation.md). |
| Opening a PR, changing CI/rules or preparing a release | [Repository governance](docs/policies/repository-governance.md) and the PR template. |

Use `gpt-6.1-sol` with ultra reasoning for decision and review subagents. Do not use `gpt-6-astra`. Before pushing, a `gpt-6-luna` high subagent runs and follows local checks and reports their outcomes. A `gpt-6-luna` medium subagent follows `main` CI after integration. Fast-forward local `main` after CI and retain a managed worktree that is the current chat checkout.
