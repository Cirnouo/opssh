# Dashboard visual reference

[Approved Visualize source](opssh-agent-dashboard.html) preserves the original bytes. Its SHA-256 is `21202CB9F2EF713FCB63544E838B75B2F4F125CC654EE67BAEC7DC5F401FC216`.

Run [the reference renderer](../../tools/Render-DashboardReference.ps1) to create a separate, sandboxed HTML file. The renderer checks the hash, supplies the prototype's icons and fixes the sample clock at 15:20 local time on 2026-11-10.

```powershell
./tools/Render-DashboardReference.ps1 -OutputPath "$env:TEMP/opssh-reference.html" -Page keys -Theme dark
```

Pass `-Scenario first-run` for the signed-out entry, `-Width 576` for the compact layout, or `-Page settings -AtBottom` for the lower settings groups. The renderer leaves the archived HTML intact.

Publish the tray on Windows x64 with `-p:OpsshDashboardFixture=true` to include the in-memory screenshot fixture. For example, launch `opssh-tray.exe --dashboard-fixture ready keys dark` from the fixture publish folder. Production publishes omit the fixture. It supplies synthetic accounts, keys, grants and activity and does not run the real provider, startup registration or OpenSSH service actions.

To capture the default-agent dialog, launch `opssh-tray.exe --dashboard-fixture default-agent-success settings dark zh-Hans` and confirm the switch in **SSH integration**. Use `default-agent-service-absent`, `default-agent-pipe-failure` or `default-agent-service-pending` for the other outcomes. The fixture paces the simulated stages for capture.

Use a 1040 × 642 DIP window for the standard comparison. Capture the browser and native windows using the same scaling and language. Crop browser chrome outside the prototype window. Native system caption controls and text rasterization follow Windows.
