# Windows visual references

## Proposals for review

- [Install and uninstall wizard](opssh-install-uninstall-preview.html) shows the native WiX layout in English and Simplified Chinese. Compare the language, license, scope, directory and Browse, confirmation, progress and result states. Use the simulator outside the installer window to vary Windows display language, token, agent ownership, failure and rollback. Uninstall enters at confirmation in the Windows display language and preserves user data.
- [Merged SSH workspace](opssh-merged-workspace-preview.html) derives from the [complete approved dashboard](opssh-desktop-ux-review.html) and reconciles the shell and controls with the current WinUI implementation and [later key reference](opssh-key-approval-preview.html). Settings and About remain in the navigation footer. All layouts include the merged 1Password and SSH keys page, Activity, Settings and About, with their visible controls and interaction states. Compare Compact summary at 880 × 520 DIP, Stacked cards at 1000 × 560 DIP and Parallel cards at 1120 × 600 DIP. The size picker shows the minimum and regular window for each layout.

The installer proposal uses the [WiX 7.0.1 WixUI_InstallDir template](https://github.com/wixtoolset/wix/blob/v7.0.1/src/ext/UI/wixlib/WixUI_InstallDir.wxs) and reuses [InstallScopeDlg](https://github.com/wixtoolset/wix/blob/v7.0.1/src/ext/UI/wixlib/InstallScopeDlg.wxs) from WixUI_Advanced. The HTML uses the pinned dialog control coordinates, banner and side artwork, and English and Simplified Chinese resources. It represents LicenseAgreementDlg, InstallDirDlg, BrowseDlg, VerifyReadyDlg, ProgressDlg, ExitDialog, FatalError, UserExit and CancelDlg. The intended MSI uses native WiX dialogs and native controls. [PowerShell's MSI](https://github.com/PowerShell/PowerShell/blob/v7.5.0/assets/wix/Product.wxs) is a precedent for extending the stock sequence with application-specific pages.

The opssh extensions are a language selector using the same banner and footer layout, scope selection before elevation with either choice leading to the destination page, a direct Remove entry with the restoration and data-preservation summary, operation history below the stock progress controls, and an independent second completion checkbox. Installation and removal work, printing and Windows UAC are simulated in the HTML. The stock geometry is in [installer units](https://learn.microsoft.com/en-us/windows/win32/msi/installer-units). Confirm the rendering at the target DPI using native Windows Installer screenshots, including system captions, the Browse control, print dialog and the real UAC state. Use the same source and environment when comparing the implementation.

For workspace captures, use the Review controls to select a scenario, theme, language and window size. Use the visualization runtime to supply the carousel and icons. Compare the simulated application window at its selected DIP size and crop the review controls. Capture both the expanded pane and the title-bar pane overlay. The browser represents Mica and the title bar using the proposal's colors and geometry. The native comparison supplies the real backdrop and system caption.

These proposals are pending approval. After selection, record the approved source digest, variant, minimum dimensions and capture scenarios here under the [Windows design workflow](../policies/repository-governance.md#windows-design-workflow).

## Archived dashboard references

These archived references describe the dashboard content and interactions. The current workspace shell uses the [WSL Settings navigation template](https://github.com/microsoft/WSL/blob/8585e23a45261be22e642562a315937a9562f1e8/src/windows/wslsettings/Views/Settings/ShellPage.xaml) with a shared Mica backdrop and the default WinUI Frame transition. Its title bar, navigation and breakpoint follow the [current behavior specification](../specs/windows-agent.md). The workspace uses the same Windows Community Toolkit SettingsControls package as [WSL Settings](https://github.com/microsoft/WSL/blob/8585e23a45261be22e642562a315937a9562f1e8/packages.config).

[The key table, diagnostic settings and signature notification preview](opssh-key-approval-preview.html) preserves the approved Visualize bytes. Its SHA-256 is `764DE30A7E3E82E58CE930E85966CB23BAE2A9CB8A7C2CAE59E394E1D92B9A42`. Pass `-Revision KeyApproval` with `-Page keys` or `-Page settings` to render that reference.

[The final review source](opssh-desktop-ux-review.html) preserves the approved Visualize bytes. Its SHA-256 is `28C0DD079609AC1F4B4A5F8497622AD381D92153A25F36CE8F02E09664AC1F98`. [The original dashboard source](opssh-agent-dashboard.html) remains available as a historical reference with SHA-256 `21202CB9F2EF713FCB63544E838B75B2F4F125CC654EE67BAEC7DC5F401FC216`.

Run [the reference renderer](../../tools/Render-DashboardReference.ps1) to create a separate, sandboxed HTML file. The renderer checks the hash, supplies the prototype's icons and fixes the sample clock at 15:20 local time on 2026-11-10.

```powershell
./tools/Render-DashboardReference.ps1 -OutputPath "$env:TEMP/opssh-reference.html" -Page keys -Theme dark
```

Pass `-Scenario first-run` for the signed-out entry, `-Width 576` for the compact layout, or `-Page settings -AtBottom` for the lower settings groups. The final source also supports `picker`, `cli-missing`, `no-access`, `vault-save`, `discover`, `cache`, and `logs` and four `-DefaultOutcome` values for the default-agent confirmation dialog. Pass `-Revision Original` to render the historical source. The renderer leaves both archives intact.

Publish the tray on Windows x64 with `-p:OpsshDashboardFixture=true` to include the in-memory screenshot fixture. For example, launch `opssh-tray.exe --dashboard-fixture ready keys dark` from the fixture publish folder. Production publishes omit the fixture. It supplies synthetic accounts, keys, grants and activity and does not run the real provider, startup registration or OpenSSH service actions.

To capture the default-agent dialog, launch `opssh-tray.exe --dashboard-fixture default-agent-success settings dark zh-Hans` and confirm the switch in **SSH integration**. Use `default-agent-service-absent`, `default-agent-pipe-failure` or `default-agent-service-pending` for the other outcomes. The fixture paces the simulated stages for capture.

Use a 1040 × 642 DIP window for the standard comparison. For the key layout use the fixture scenario `key-approval` and compare 930, 700, 576 and 540 DIP widths in addition to the standard size. The `key-preload-failure` and `key-preload-partial` scenarios allow the failed rows to be retried. A fixture executable launched with `--dashboard-reference <absolute HTML path>` opens the rendered reference in a separate WebView2 window behind the current desktop windows. Capture the browser and native windows using the same scaling and language. Crop browser chrome outside the prototype window. Native system caption controls and text rasterization follow Windows.

## Signing notification fixture

Use `--dashboard-fixture signing-toast keys dark zh-Hans` to show the app approval, request-only approval and denial actions, or `signing-toast-once` with the approval cache off. Both use a synthetic key and the fixture process identity. Run native notification checks on an isolated Windows desktop because Windows owns the banner and notification center. The archived KeyApproval renderer preserves the earlier four-action toast design.
