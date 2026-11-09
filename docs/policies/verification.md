# Verification policy

Use behavior tests with generated in-memory keys, controlled clocks and fake provider, process, transport and storage boundaries. Exercise real Windows process or pipe behavior where the assertion depends on it. Cover changed failures, cancellation, cleanup and compatibility paths. Follow the affected [behavior specification](../specs/windows-agent.md) for its contract.

## Complete Windows validation

Use [`./tools/Test-Windows.ps1`](../../tools/Test-Windows.ps1) on Windows x64 with the pinned Go and .NET versions and PowerShell 7. It checks Go formatting and modules, runs Go tests and vet, uses locked .NET restore, runs tray tests and exercises Go/tray interop with the fixture core. It then builds a fresh Windows Release folder with the production core and checks that the expected executables and tray runtime resources exist and are nonempty. Use the output from the current successful invocation as evidence for the intended sources. Distinguish fixture interop and artifact inspection from any runtime smoke checks performed on the production distributable.

Ordinary behavioral checks use no signed-in vault, real SSH connection, system SSH settings changes or administrator privileges. Live provider validation requires authorization for the exact disposable test item and procedure. Follow the [1Password Desktop procedure](../references/1password-desktop-validation.md) and keep credentials and secret-bearing output out of evidence.

## Local commit gate

Use the tracked [pre-commit](../../.githooks/pre-commit) and [commit-msg](../../.githooks/commit-msg) hooks. See [worktree setup](../../CONTRIBUTING.md#prepare-a-worktree). Stage only the intended files, inspect the staged diff and commit normally. [`Test-StagedTree.ps1`](../../tools/Test-StagedTree.ps1) archives the exact index tree, runs the complete Windows driver in the isolated snapshot and writes a successful receipt after rechecking the tree. The commit-msg hook checks and consumes the receipt for the same tree. A failed run or changed staged tree requires fresh validation. The full gate runs at commit time. The commit-msg phase only checks the receipt.

Run and follow the final local checks before push as required by [repository governance](repository-governance.md#branch-and-pull-request-lifecycle). Verify the actual staged or committed tree being reported, and rerun affected checks after the final code, resource or documentation edit. For documentation changes, check UTF-8, relative links, anchors and any affected repository assertions.

## CI and evidence

The [Windows workflow](../../.github/workflows/windows.yml) runs the complete Windows driver independently on PR and `main` events. Report the PR head, the tested merge SHA and the exact `main` commit separately. The commit receipt, local driver, PR run and `main` run each cover their own source state.

Report the files or revision tested, commands, outcomes, platform paths exercised and material limits. For a failure, identify the failed phase and affected dependencies, and rerun the relevant checks on the final state. Record focused rerun results separately from any failed or incomplete full run. Distinguish checks that failed, were skipped or were unavailable, and keep live-provider evidence separate from ordinary validation.
