# Dashboard visual reference

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
