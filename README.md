# opssh

**English** · [简体中文](README.zh-Hans.md)

opssh is an SSH agent for the built-in Windows x64 OpenSSH client. Its Go core fetches selected keys on demand from the signed-in 1Password desktop app through the official 1Password Go SDK and signs in memory. The resident C# WinUI 3 tray shows process ancestry and key fingerprints and manages approval for specific requests.

- Approve a single request, the requesting process, or a host process instance and its descendants. Windows PID and creation time identify the instance.
- Private keys default to a fixed five-minute cache. Host approvals default to a fixed ten-minute cache. The two policies are independent.
- Connecting to 1Password automatically indexes public keys. By default, the index includes usable SSH Key items in vaults the account can access. You can adjust the scope by vault and item. The Go core manages private keys, signing, and approvals.
- The per-logon agent pipe can coexist with the system agent.

Offline verification uses generated keys and a mock provider. Production builds use the Desktop SDK adapter. Reading real 1Password items requires the [separate integration procedure](docs/references/1password-desktop-validation.md).

## Build and use

Install Go 1.27.3, .NET 10 SDK 10.0.403, and PowerShell 7. Run this command in a Windows x64 checkout.

```powershell
./tools/Test-Windows.ps1
```

The script produces a verified, self-contained `release` directory. Launch its `opssh-tray.exe` and open Settings from the tray. Under **1Password & keys**, enter the account name or ID displayed in the 1Password desktop sidebar and select **Connect**. Add the account to 1Password Desktop and enable **Settings → Developer → Integrate with 1Password SDKs** there first. The official 1Password window handles verification and read authorization.

After connecting, opssh reads the public keys of SSH Key items in scope. Set the account default and adjust the scope by vault and item name. Item rules take precedence over vault rules. Unset rules inherit the level above. New configurations include all by default. Existing manually configured selections keep their original item scope.

After a restart, opssh loads the saved account and public index. You can use SSH without opening Settings and selecting **Connect** again. If an approved signature needs a private key, opssh requests it from 1Password. Approve the official 1Password prompt when the SDK requires a fresh grant.

In the PowerShell session where you want to use opssh, record the current `$env:SSH_AUTH_SOCK` value and run the command shown under **Settings → Overview → SSH client setup → PowerShell → Show command**. Use the built-in OpenSSH client's `ssh-add -L` to list public keys. An `IdentityAgent` setting in your SSH configuration may take precedence over the environment variable. To revert, close the shell or restore its previous value. The command affects only the current process and its descendants.

Closing Settings returns to the tray. **Quit opssh** stops both processes. Locking opssh clears signing approvals and cached private keys. Refresh the public key index from the tray after adding, rotating, archiving, or deleting items in 1Password.

## Language and theme

In **Appearance**, choose English (`en`), Simplified Chinese (`zh-Hans`), or **Follow system**. The default uses the Windows display language when the tray starts and falls back to English for other languages. Choose light, dark, or system theme. Both preferences are kept when the tray restarts.

## Updates

In **About**, select **Check for updates** to look for a newer stable GitHub release. **Automatically check for updates** is enabled by default. It checks once per tray launch and shows a Windows notification if a newer version is available. Open the release page from **About** to view it.

[Behavior and security boundaries](docs/specs/windows-agent.md) · [Development and verification](CONTRIBUTING.md) · [Offline performance measurements](docs/references/windows-offline-performance.md)
