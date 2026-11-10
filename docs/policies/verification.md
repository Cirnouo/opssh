# Verification policy

Use behavior tests with generated in-memory keys, controlled clocks and fake provider, process, transport and storage boundaries. Exercise real Windows process or pipe behavior where the assertion depends on it. Cover changed failures, cancellation, cleanup and compatibility paths. Follow the affected [behavior specification](../specs/windows-agent.md) for its contract.

## Complete Windows validation

Use [`./tools/Test-Windows.ps1`](../../tools/Test-Windows.ps1) on Windows x64 with the pinned Go and .NET versions and PowerShell 7. It checks Go formatting and modules, runs Go tests and vet, uses locked .NET restore, runs tray tests and exercises Go/tray interop with the fixture core. It then builds a fresh Windows Release folder with the production core and checks that the expected executables and tray runtime resources exist and are nonempty. Use the output from the current successful invocation as evidence for the intended sources. Distinguish fixture interop and artifact inspection from any runtime smoke checks performed on the production distributable.

Ordinary behavioral checks use no signed-in vault, real SSH connection, system SSH settings changes or administrator privileges. Live provider validation requires authorization for the exact disposable test item and procedure. Follow the [1Password Desktop procedure](../references/1password-desktop-validation.md) and keep credentials and secret-bearing output out of evidence.

## Local commit gate

Install and check the [worktree-private commit hooks](../../tools/Install-TestHooks.ps1) before committing. See [worktree setup](../../CONTRIBUTING.md#prepare-a-worktree). Reinstall after changing the gate or the prior hook set. Git runs configured hooks in its usual order. The installed pre-commit runs the prior hook-directory entry before capturing the index. The installed commit-msg runs the prior hook-directory entry before checking the receipt. Git's exit-code probes establish installation, and a normal commit exercises the product gate.

Stage only the intended files, inspect the staged diff and commit normally. The gate captures the actual index and shared-index bytes, computes the tree from copies and writes its raw blobs into a fresh snapshot. It runs the staged Windows driver in an owned Windows process tree with a 25-minute validation deadline. It checks the complete set of reported phases and hashes the fresh release files before admitting success. The receipt binds the Git invocation, index identities and hashes, tree and completed report. The final hook rechecks the index, report and artifacts, then consumes the receipt. A failed, incomplete or changed result requires fresh validation. The final hook checks evidence without repeating tests.

The gate records phase outcomes and artifact digests in worktree-private Git state. Report the tree, required checks and outcome. Report an actual commit SHA only after Git commits successfully. Keep raw child diagnostics and personal paths out of retained evidence.

Run and follow the final local checks before push as required by [repository governance](repository-governance.md#branch-and-pull-request-lifecycle). Verify the actual staged or committed tree being reported, and rerun affected checks after the final code, resource or documentation edit. For documentation changes, check UTF-8, relative links, anchors and any affected repository assertions.

## CI and evidence

The [Windows workflow](../../.github/workflows/windows.yml) runs the complete Windows driver independently on PR and `main` events. Report the PR head, the tested merge SHA and the exact `main` commit separately. The commit receipt, local driver, PR run and `main` run each cover their own source state.

Report the files or revision tested, commands, outcomes, platform paths exercised and material limits. For a failure, identify the failed phase and affected dependencies, and rerun the relevant checks on the final state. Record focused rerun results separately from any failed or incomplete full run. Distinguish checks that failed, were skipped or were unavailable, and keep live-provider evidence separate from ordinary validation.

## Windows distribution checks

Build the release folder with `./tools/Test-Windows.ps1`. The driver evaluates the tray project `Version`, links the same version into the Go core and checks the published tray assembly. Supply `-ReleaseVersion M.m.p` when comparing with an expected release version. The driver keeps its ordinary offline provider and phase coverage.

`Windows distributables` takes the exact successful Release folder and offline handshake driver from `Windows offline build and tests` in the same workflow run. It verifies the downloaded file inventory, lengths and SHA-256 against the offline job's provenance before building the packages. The check verifies the distribution notice inventory against the product dependencies. [Windows workflow](../../.github/workflows/windows.yml)

The build uses WiX 7.0.1 and its pinned Util extension. Accept the WiX EULA on the WiX commands that restore and build the installer. `Build-WindowsPackages.ps1` generates the MSI. `Test-WindowsPackages.ps1` checks scope and table identity, completion dialog controls and their conditions, ICE validation, the version and extracted payload bytes. The first-party binaries and MSI are unsigned. [WiX build-scoped EULA acceptance](https://docs.firegiant.com/wix/osmf/)

Use the GitHub-hosted Windows x64 lifecycle check for installation. The disposable runner temporarily enables unmanaged per-user MSI installation and verifies that it restored the original Installer policy. The check verifies the medium-integrity standard-user token and uses a source-built alternate version in a disposable profile. It checks silent install, repair, upgrade, downgrade refusal, running-app refusal, uninstall and user data preservation. Verify interactive completion separately on a disposable Windows desktop. It verifies the installed production core's version through the real tray control handshake in a fresh data directory. The handshake leaves provider configuration empty and exits the core through the control channel. Retain the sanitized lifecycle report and the package evidence for the exact run.
