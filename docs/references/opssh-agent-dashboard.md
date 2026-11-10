# Windows visual references

## Proposals for review

- [Install and uninstall wizard](opssh-wix-elevation-preview.html) retains the accepted WiX layout and revises the timing of consent. Compare the language, license, scope, directory and Browse, confirmation, consent, progress and result states. Use the simulator outside the installer window to vary Windows display language, token, agent ownership, failure and rollback. Uninstall enters at confirmation in the Windows display language and preserves user data.
- [Stacked cards workspace](opssh-stacked-workspace-preview.html) applies the selected layout to the complete dashboard. Compare the 1240 × 700 DIP regular window and the 1000 × 560 DIP minimum. Capture the Application filter before and after typing, with checked choices both inside and outside the search results, and the Time header in both directions. Activity starts newest first and alternates direction on each Time click. Typing narrows the list of choices, and the checked applications determine which activity rows match.

The installer proposal uses the [WiX 7.0.1 WixUI_InstallDir template](https://github.com/wixtoolset/wix/blob/v7.0.1/src/ext/UI/wixlib/WixUI_InstallDir.wxs) and reuses [InstallScopeDlg](https://github.com/wixtoolset/wix/blob/v7.0.1/src/ext/UI/wixlib/InstallScopeDlg.wxs) from WixUI_Advanced. The HTML uses the pinned dialog control coordinates, banner and side artwork, and English and Simplified Chinese resources. It represents LicenseAgreementDlg, InstallDirDlg, BrowseDlg, VerifyReadyDlg, ProgressDlg, ExitDialog, FatalError, UserExit and CancelDlg. The intended MSI uses native WiX dialogs and native controls. [PowerShell's MSI](https://github.com/PowerShell/PowerShell/blob/v7.5.0/assets/wix/Product.wxs) is a precedent for extending the stock sequence with application-specific pages.

The opssh extensions are a language selector using the same banner and footer layout, scope selection before elevation with either choice leading to the destination page, a direct Remove entry with the restoration and data-preservation summary, operation history below the stock progress controls, and an independent second completion checkbox. The stock scope radio group has no shield. The [confirmation dialog](https://github.com/wixtoolset/wix/blob/v7.0.1/src/ext/UI/wixlib/VerifyReadyDlg.wxs) uses [ElevationShield](https://learn.microsoft.com/en-us/windows/win32/msi/elevationshield-attribute) on the action that requires elevation. The preview uses the stock light appearance. The stock [MSI dialog style bits](https://learn.microsoft.com/en-us/windows/win32/msi/dialog-style-bits) have no system dark mode setting.

In the proposal, Install or Remove opens the simulated Windows consent prompt immediately when elevation is required. Approval opens progress. Cancellation ends the operation before any progress or changes. Capture those states alongside per-user, already elevated, and OpenSSH restoration cases. Stock WiX sequences [ProgressDlg](https://github.com/wixtoolset/wix/blob/v7.0.1/src/ext/UI/wixlib/ProgressDlg.wxs) before ExecuteAction. Verify the exact native dialog order, single prompt and cancellation on the isolated Windows desktop. Installation, removal, printing and UAC are simulated in the HTML. Confirm native dialog geometry, including system captions, Browse and printing, at the same DPI. [Windows Installer units](https://learn.microsoft.com/en-us/windows/win32/msi/installer-units)

For workspace captures, use the Review controls to select a scenario, theme, language and window size. Use the visualization runtime to supply the icons. Compare the simulated application window at its selected DIP size and crop the review controls. Capture both the expanded pane and the title-bar pane overlay. The browser represents Mica and the title bar using the proposal's colors and geometry. The native comparison supplies the real backdrop and system caption.

The installer appearance and Stacked cards layout were selected from the preceding proposals. The revised consent flow and workspace interactions are pending review under the [Windows design workflow](../policies/repository-governance.md#windows-design-workflow).

## Archived dashboard references

[The three workspace options](opssh-merged-workspace-preview.html) preserve the Stacked cards selection source at SHA-256 `3191AC013188CBB2431C25CB2D2004B575A7516441CC098DC54F496DE879DA45`. [The preceding WiX proposal](opssh-install-uninstall-preview.html) preserves the accepted installer appearance at SHA-256 `77E0A937A0B68023BAF2925C3DDA5E94189DFD1C917B7F95BB84A855B059A5D7`.

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
