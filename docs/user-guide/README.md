# opssh user guide

**English** · [简体中文](README.zh-Hans.md) · [Project overview](../../README.md)

Use opssh to sign with SSH keys from 1Password Desktop in the built-in Windows x64 OpenSSH client. Start with [installation](#install), then [connect 1Password](#connect), [select the agent in PowerShell](#select-agent), and [approve your first request](#approve-signing).

<a name="install"></a>

## Install and launch opssh

You need Windows x64 with the built-in OpenSSH client, PowerShell, and a signed-in 1Password desktop app with an SSH Key item. For your first connection, use an SSH host that already accepts the item's public key.

When a stable release is available, choose a Windows x64 download. The installers and opssh binaries are unsigned. [GitHub Releases](https://github.com/Cirnouo/opssh/releases)

| Download | Install and launch |
| --- | --- |
| MSI or offline installer EXE | Run either installer. It installs for the current user under `%LOCALAPPDATA%\Programs\opssh`. Open **opssh** from the Start menu. |
| Portable ZIP | Extract the archive to a folder and run `opssh-tray.exe` there. Keep the extracted files together. |

If opssh is running, right-click the tray icon and choose **Quit opssh** before installing, repairing, upgrading, or uninstalling. For an installed copy, use Windows **Installed apps** to manage the application. Uninstall removes the application files and Start menu shortcut. It keeps your settings and public key index under `%LOCALAPPDATA%\opssh`.

For a source checkout, see [development and contributions](../../CONTRIBUTING.md).

<a name="connect"></a>

## Connect 1Password and list keys

1. Sign in to 1Password Desktop. Install the official 1Password CLI. Turn on Windows Hello and enable **Integrate with 1Password CLI** and **Integrate with 1Password SDKs** in **Settings → Developer**. [CLI desktop integration](https://www.1password.dev/cli/app-integration) · [Desktop SDK integration](https://www.1password.dev/sdks/desktop-app-integrations)
2. Open opssh from the tray and choose **Connect 1Password**. The picker selects the first available account and displays its email, with the user name when available. Choose **Continue in 1Password** and complete any authorization prompt.
3. On **1Password**, select the vaults allowed for discovery. Then open **SSH keys** and choose **Discover SSH keys**. In a narrow window, use the navigation button in the title bar to switch pages.

opssh saves the selected account and its public index. The 1Password page shows the outcome and local time of the last Desktop SDK request. 1Password may ask for authorization again on the next request. Switching accounts or choosing **Remove account** clears the previous account’s scope, index, cached keys, approvals, and activity.

<a name="select-agent"></a>

## Select or restore the SSH agent

1. In the PowerShell window where you want to use opssh, save the current agent selection.

   ```powershell
   $previousSshAuthSock = $env:SSH_AUTH_SOCK
   ```

2. In opssh, open **Settings → SSH integration → Current PowerShell session**. Choose **Copy command**, then paste the displayed command into that shell and run it.
3. Run `ssh-add -L` with the built-in OpenSSH client and check that it lists the expected public key. If SSH uses a different agent, check your SSH configuration for `IdentityAgent`, which can override `SSH_AUTH_SOCK`.

The command affects the current PowerShell process and processes it starts. To restore the previous selection, close that shell or run the following in the same shell.

```powershell
$env:SSH_AUTH_SOCK = $previousSshAuthSock
```

Turn on **Use as the system default SSH agent** in **Settings → SSH integration** to use the Windows default pipe. The dialog shows the steps as they run. Windows may ask for administrator approval to stop and disable the running automatic Windows OpenSSH agent. Quitting opssh leaves the default selection in place and releases the pipe. To restore the Windows service, turn off the switch and choose **Restore OpenSSH** before uninstalling opssh.

In **Settings → Startup**, **Wait for 1Password to start** shows a Windows notification when 1Password is ready. Open it, select an account, and choose **Continue in 1Password** to request authorization. Selecting your current account keeps its scope, keys, approvals, and activity. Disable **Start at Windows sign-in** if you are removing a portable copy.

<a name="approve-signing"></a>

## Approve an SSH signature

Connect to a host that accepts one of the listed public keys with your usual `ssh` command. Check the application, PID and key fingerprint in the opssh Windows notification. SSH agent requests provide no verified remote destination.

| Choice | What you approve |
| --- | --- |
| **Allow this app** | Reuse the displayed key for the running app and its verified descendants. If opssh finds no eligible window owner, reuse covers only the verified requester process. |
| **Allow once** | The pending signature. |
| **Deny** | Reject the pending signature. |

Turn approval caching off to show only the request and deny options. To revoke an app approval, expand that key in **SSH keys**. If Windows disables or rejects the notification, opssh refuses the request and records the outcome in Activity. A notification that you dismiss expires at the request deadline.

If the approved request needs a private key, opssh fetches it from 1Password. Complete a separate authorization in the 1Password window if prompted. Closing the opssh Settings window leaves the tray and agent running. **Quit opssh** stops both.

<a name="key-scope"></a>

## Choose and refresh keys

Allow or exclude each vault on **1Password**. Changes take effect immediately. Excluding a vault withdraws its public keys and clears their cached private keys and approvals. Including a vault makes it eligible for the next manual **Discover SSH keys**. Existing item rules apply until you explicitly change the containing vault's choice. 1Password manages account-wide read permission separately.

After adding, rotating, archiving, or deleting a key in 1Password, run **Discover SSH keys** again. If the scope is too broad, narrow it to at most 64 keys.

<a name="caches-and-lock"></a>

## Manage cached keys and approvals

In **Settings → Cache policy**, configure the private key and process approval caches independently. Choose **Do not cache**, **Fixed duration**, **Idle timeout**, or **Until opssh exits**. The defaults are five minutes for loaded private keys and ten minutes for process approvals. Idle time renews after successful signing. A process approval expires when its process ends.

Use **SSH keys** to preload or clear one or all private keys. If a preload fails, check the reason beside the key and retry after resolving it. Bulk preload keeps keys that loaded successfully. To diagnose a failed request, enable logs at **DEBUG** before retrying. Expand a public key to view the process name, PID and local expiry of each approval. Revoke an individual approval, all approvals for that key, or all approvals from the toolbar. Locking 1Password ends its permission for new reads. Cached private keys follow the opssh policy.

## Review activity

The **Activity** page displays recent signing outcomes in this opssh session. Select multiple values for application, key, result or local date. Values within a filter group are alternatives. All active groups must match. Hold Shift while wheeling over a long command to scroll it horizontally, then continue at its edge to move the table. Command lines can contain sensitive values and only appear in the local activity window. **Clear activity** discards the session history.

<a name="diagnostic-logs"></a>

## Inspect diagnostic logs

Open **Settings → Logs** to turn on local diagnostics. Logging starts off and uses **INFO** by default. Signing and preload completions can show the steps the request reached, with a duration and outcome for each. For a timed-out signing request, check whether the last step was approval or waiting for private key material. The log excludes private keys, account details, process details and SSH inputs. Use **Open directory** to open the log folder directly in Windows File Explorer. The folder can contain the current log and up to two rotated files. Each file is capped at 1 MiB. **Clear logs** removes the existing files after confirmation. Turn logging off to stop new entries.

<a name="appearance-and-updates"></a>

## Set appearance and check for updates

In **Settings → Preferences**, choose English, Simplified Chinese, or **Follow system**. The default follows the Windows display language when the tray starts and uses English for other languages. Choose a light, dark, or system theme. Preferences survive a restart. The window reloads when the language changes.

In **About**, choose **Check for updates** to look for a newer stable release. **Automatically check for updates** is on by default. It checks once per tray launch and shows a Windows notification when a newer version is available. Use **Open GitHub Releases** in About to view the release, then follow the [installation steps](#install) to upgrade.

<a name="troubleshooting"></a>

## Troubleshoot

| Symptom | What to do |
| --- | --- |
| The tray opens but the core is unavailable | Use **Restart core** from the tray if available. For a portable copy, check that you extracted the entire ZIP. |
| Connect, refresh, or key loading is denied or times out | Check the account, unlock 1Password Desktop, check SDK integration, and complete any prompt there. Retry the affected action. If reads keep timing out after those checks, quit and reopen opssh to restart the core. |
| The expected key is missing or fails after rotation | Confirm it is an active SSH Key item, check the vault selection, then discover keys again. opssh supports Ed25519 and 2048–4096 bit RSA keys with SHA-2 signing. |
| SSH uses another agent or cannot reach opssh | Check that the tray is running, then rerun the displayed command in the affected PowerShell session. Check whether the host's SSH configuration has an `IdentityAgent` override. |
| SSH lists the key but cannot sign | Retry SSH and complete the signing approval and any separate 1Password prompt. |
| SSH still rejects the login after signing | Check the username and the public key authorized by that host. |

For help, include the opssh version, Windows version, the affected step and sanitized feedback. Keep private keys, credentials, account and vault IDs, personal paths, SSH destinations, full configuration files, and raw provider errors private. [Support](../../SUPPORT.md) · [Private vulnerability reporting](../../SECURITY.md)
