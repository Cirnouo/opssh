# Stable Windows releases

Use a separately authorized release task. Confirm the latest published stable release before changing `CHANGELOG.md`. Collect cumulative user-visible changes in `Unreleased`. When preparing a release, move the intended entries into a unique `## [M.m.p] - YYYY-MM-DD` section with at least one change bullet, and leave a fresh `Unreleased` section. Keep published sections intact. The first release has no published comparison baseline. [Changelog policy](documentation.md#changelog)

## Source, version and tag

Use a source commit already on remote `main` with successful push CI at that exact SHA. Set the tray project `Version` to the intended `M.m.p` and verify the packages from that commit. Use numeric components in the common MSI and .NET range up to `255.255.65534`. The build gives the same version to the Go core and checks the tray assembly, production core handshake, MSI and Burn versions. [Windows verification](verification.md)

Before creating a release tag, confirm the attribution requirements for the exact embedded 1Password WASM. Record the reviewed upstream notice inventory or confirmed redistribution basis in `third_party/licenses/manifest.json`, including the WASM SHA-256 and a public evidence or sanitized review link. Add any corresponding notice files to the verified inventory. Tag admission rejects absent or stale evidence. [Third-party notices](../../THIRD-PARTY-NOTICES.md)

Review the distribution requirements of the pinned Windows App SDK and Windows SDK for all three formats before public publication. Record how the applicable terms are presented to and accepted by downstream distributors and end users. [Third-party notices](../../THIRD-PARTY-NOTICES.md)

Before tagging, read the repository's immutable release setting with a maintainer credential. Require `gh api repos/Cirnouo/opssh/immutable-releases --jq .enabled` to return `true`. The publisher uses a job-scoped contents token and requires an immutable result on publication readback. [Immutable release settings API](https://docs.github.com/en/rest/repos/repos#check-if-immutable-releases-are-enabled-for-a-repository)

Verify that the repository rule protects `refs/tags/v*` from updates and deletion. After release authorization, create and push one new annotated `vM.m.p` tag using the configured SSH signing key. GitHub must verify the signature on the tag object. The stable release workflow matches the pushed `after` object and the target `head_commit.id` separately, then verifies the direct remote target, current `main` ancestry and exact source CI. Choose a new version if an authorized published source needs replacement. [Release workflow](../../.github/workflows/release.yml) · [GitHub tag push payload](https://github.blog/changelog/2021-10-01-the-push-webhook-payload-provides-a-consistent-head_commit-on-tag-creation/)

## Build and publication

The tag workflow reruns Windows verification, builds and tests the actual packages, and reruns the dependency audit at the admitted source. It publishes the Windows x64 MSI, offline installer EXE and portable ZIP with filenames `opssh-M.m.p-windows-x64.{msi,exe,zip}`. The Windows binaries and installers are distributed unsigned. The SSH signature identifies the Git tag, and the release notes provide package SHA-256 checksums.

Publication is serialized across releases in this repository. The publisher downloads the exact verified artifact ID from the same run and checks the manifest and release notes against the validation job's SHA-256 outputs. It verifies the three asset names, sizes and digests. A draft records the tag object, source commit and both SHA-256 values before any upload, so a retry resumes only the same inputs even if some assets are still missing. The publisher verifies the full asset set and the tag before publishing. The resulting release must be immutable. Only the publication job can write repository contents. [GitHub immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases)

## Failure and retries

After a transient publication failure, rerun only the publication job to reuse the original verified artifact. An existing published release is a successful skip only when its tag, classification and three assets match. If a tag, draft, asset or artifact conflicts, keep the draft and original evidence for maintainer review. A full rebuild can yield different package bytes. Compare the recorded asset metadata, tag object and direct source commit before deciding how to recover. [GitHub job reruns](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/re-run-workflows-and-jobs)

Exercise the publisher and admission protocol with `./tools/Test-WindowsReleasePublisher.ps1` and its fake GitHub boundary. Windows PR and `main` CI build and verify the actual packages and run the fake protocol cases. Keep real tag, signing and publication evidence separate from local and CI simulations.
