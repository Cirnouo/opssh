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

1. Sign in to the account in 1Password Desktop. In 1Password, open **Settings → Developer** and turn on **Integrate with 1Password SDKs**. [1Password Desktop SDK integration](https://www.1password.dev/sdks/desktop-app-integrations)
2. Right-click the opssh tray icon and choose **Open opssh**. Go to **1Password & keys** and enter the account name or ID shown in the 1Password sidebar.
3. Select **Connect** and complete any verification or authorization in the 1Password window. Check **Usable SSH keys** for the expected key.

On a new configuration, opssh indexes usable SSH Key items in all vaults the account can access. To start with an empty index, set the account default in **Discovery scope** to **Exclude all** and select **Apply scope** before connecting. Then [include only the keys you want](#key-scope). The public key list is saved with the account and restored when opssh starts. The next operation that reads from 1Password may ask for a fresh authorization.

<a name="select-agent"></a>

## Select or restore the SSH agent

1. In the PowerShell window where you want to use opssh, save the current agent selection.

   ```powershell
   $previousSshAuthSock = $env:SSH_AUTH_SOCK
   ```

2. In opssh, open **Overview → SSH client setup → PowerShell → Show command**. Copy the displayed command into that shell and run it.
3. Run `ssh-add -L` with the built-in OpenSSH client and check that it lists the expected public key. If SSH uses a different agent, check your SSH configuration for `IdentityAgent`, which can override `SSH_AUTH_SOCK`.

The command affects the current PowerShell process and processes it starts. To restore the previous selection, close that shell or run the following in the same shell.

```powershell
$env:SSH_AUTH_SOCK = $previousSshAuthSock
```

<a name="approve-signing"></a>

## Approve an SSH signature

Connect to a host that accepts one of the listed public keys with your usual `ssh` command. When opssh asks for approval, check the key fingerprint and requesting process. Use the process chain to identify an ancestor before approving it. SSH agent requests provide no verified remote destination.

| Choice | What you approve |
| --- | --- |
| **Allow once** | The pending signature. |
| **Allow this client** | Reuse for the same key and the same requesting process, within your approval cache policy. |
| **Allow selected ancestor** | Reuse for the same key by the selected running process and its verified descendants, within your approval cache policy. |
| **Deny** | Reject the pending signature. |

If the approved request needs a private key, opssh fetches it from 1Password. Complete a separate authorization in the 1Password window if prompted. Closing the opssh Settings window leaves the tray and agent running. **Quit opssh** stops both.

<a name="key-scope"></a>

## Choose and refresh keys

In **1Password & keys → Discovery scope**, choose the account default and set overrides for individual vaults or SSH Key items. An item rule overrides its vault rule. **Inherit** uses the nearest rule above it. Select **Apply scope** to save. Configurations migrated from older manual key selections retain their original scope. These rules control the keys opssh indexes and uses. 1Password separately manages account-wide read permission.

After adding, rotating, archiving, or deleting a key in 1Password, go to **1Password & keys → Public index → Refresh index**. Check the updated key list before retrying SSH. If the scope is too broad, narrow it to at most 64 keys. If Settings reports a vault or item discovery limit, follow the displayed guidance and refresh again.

<a name="caches-and-lock"></a>

## Manage cached keys and approvals

In **Approval & caches**, configure the private-key cache and approval cache separately under **Change cache policies**. The defaults are a fixed five minutes for loaded private keys and a fixed ten minutes for process approvals. Choose **Off**, **Fixed duration**, **Idle timeout**, or **Until lock** for each, then select **Apply cache settings**. A fixed timer starts when a key loads or a request is approved. An idle timer renews after successful signing.

- To load a key ahead of time, select it under **1Password & keys → Usable SSH keys** and choose **Preload selected**.
- To clear loaded private keys, use **Evict loaded keys**. Process approvals can still be reused.
- To require new approval, use **Revoke all approvals** or choose a key on **1Password & keys** and use **Revoke selected** under **Approval & caches**.
- To clear both and pause signing, choose **Overview → Agent lock → Lock**. The public keys can still be listed. Choose **Unlock** before signing again.

Locking 1Password ends its permission for new reads. Keys already loaded by opssh follow its own cache policy. Lock opssh as well when you want to clear both loaded keys and approvals.

<a name="appearance-and-updates"></a>

## Set appearance and check for updates

In **Appearance**, choose English, Simplified Chinese, or **Follow system**. The default follows the Windows display language when the tray starts and uses English for other languages. Choose a light, dark, or system theme. Preferences survive a restart. Apply any pending Settings edits before changing the language because the window reloads.

In **About**, choose **Check for updates** to look for a newer stable release. **Automatically check for updates** is on by default. It checks once per tray launch and shows a Windows notification when a newer version is available. Use **Open GitHub Releases** in About to view the release, then follow the [installation steps](#install) to upgrade.

<a name="troubleshooting"></a>

## Troubleshoot

| Symptom | What to do |
| --- | --- |
| The tray opens but the core is unavailable | Use **Restart core** from the tray if available. For a portable copy, check that you extracted the entire ZIP. |
| Connect, refresh, or key loading is denied or times out | Check the account, unlock 1Password Desktop, check SDK integration, and complete any prompt there. Retry the affected action. If reads keep timing out after those checks, quit and reopen opssh to restart the core. |
| The expected key is missing or fails after rotation | Confirm it is an active SSH Key item, check the vault and item scope, then refresh. opssh supports Ed25519 and 2048–4096 bit RSA keys with SHA-2 signing. |
| SSH uses another agent or cannot reach opssh | Check that the tray is running, then rerun the displayed command in the affected PowerShell session. Check whether the host's SSH configuration has an `IdentityAgent` override. |
| SSH lists the key but cannot sign | Check whether opssh is locked, retry SSH, and complete the signing approval and any separate 1Password prompt. |
| SSH still rejects the login after signing | Check the username and the public key authorized by that host. |

For help, include the opssh version, Windows version, the affected step and sanitized feedback. Keep private keys, credentials, account and vault IDs, personal paths, SSH destinations, full configuration files, and raw provider errors private. [Support](../../SUPPORT.md) · [Private vulnerability reporting](../../SECURITY.md)
