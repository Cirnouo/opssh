# Repository governance

Use focused Conventional Commit subjects and topic branches without a `codex/` prefix. Use PRs to `main`, linear history and squash merging. Keep the PR number out of the PR title and retain `(#number)` in the final squash subject. Report the final behavior, actual checks and compatibility limits. Preserve unrelated repository settings and distinguish permission to configure publication from permission to publish a release.

Enable and verify actual Windows build, test, distributable and dependency or scanning checks before requiring their emitted check contexts. Validate every new PR and `main` commit independently. Do not copy check names or build scripts from another project. Verify the remote state before changing protected rules or other settings. Review published release baselines and verify the exact distributable before creating an authorized release. Keep pull-request and main validation separate from publication.
