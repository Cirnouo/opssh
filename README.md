# opssh

**English** · [简体中文](README.zh-Hans.md)

opssh is an SSH agent for the built-in Windows x64 OpenSSH client. It uses SSH keys from 1Password Desktop to sign in memory. A Windows notification shows the application, PID and key fingerprint so you can decide whether to approve a signature.

- Approve one signature, reuse a key for the displayed application, or reuse that key for all verified applications in your Windows logon.
- Choose which 1Password vaults and SSH Key items opssh makes available to SSH.
- Select opssh in the PowerShell sessions where you want it. The Windows system agent can run alongside it.

<a name="build-and-use"></a>
<a name="install-from-a-stable-release"></a>

## Install

When a stable release is available, download the Windows x64 MSI, offline installer EXE, or portable ZIP. The MSI and EXE install for the current user. Open **opssh** from the Start menu, or extract the ZIP and run `opssh-tray.exe` from the extracted folder. The installers and opssh binaries are unsigned. [GitHub Releases](https://github.com/Cirnouo/opssh/releases)

To run opssh before the first stable release, follow the [source build steps](CONTRIBUTING.md#verify-and-submit).

If opssh is running, choose **Quit opssh** from the tray before installing, repairing, upgrading, or uninstalling. See the [installation and maintenance steps](docs/user-guide/README.md#install) for the install location and what uninstall keeps.

## Quick start

1. Sign in to 1Password Desktop and install the official CLI. Enable CLI and SDK integration in 1Password.
2. Open opssh from the tray, select an account, and complete 1Password verification. On **1Password**, allow the vaults you want to use. On **SSH keys**, select **Discover SSH keys**.
3. Copy the PowerShell command from **Settings → SSH integration** into the shell where you want to use opssh. Run `ssh-add -L` and check the public key before approving a signing request.


## User guide

[Read the English user guide](docs/user-guide/README.md) for key scope, approvals, cache settings and recovery.

<a name="language-and-theme"></a>
<a name="updates"></a>

[Select or restore the SSH agent](docs/user-guide/README.md#select-agent) · [Appearance and updates](docs/user-guide/README.md#appearance-and-updates) · [Troubleshooting](docs/user-guide/README.md#troubleshooting)

## FAQ

**Do I need to connect again after restarting opssh?**

opssh restores the saved account and public key index. The next read from 1Password may require a new authorization in its desktop window.

**Can I use a different agent in another shell?**

Yes. The displayed PowerShell command changes the agent for that shell and processes it starts. Check whether your SSH configuration sets `IdentityAgent`, which can override the environment variable.

**Does clearing private keys also revoke approvals?**

Use the separate **Clear loaded private keys** and **Revoke all approvals** controls on **SSH keys**. 1Password manages Desktop access independently.


## Resources and support

[1Password Desktop SDK integration](https://www.1password.dev/sdks/desktop-app-integrations) · [Windows agent behavior and security boundaries](docs/specs/windows-agent.md) · [Offline performance measurements](docs/references/windows-offline-performance.md) · [Changelog](CHANGELOG.md)

[Support](SUPPORT.md) · [Private vulnerability reporting](SECURITY.md) · [Build, verify and contribute](CONTRIBUTING.md)

## License

[Apache License 2.0](LICENSE) · [Third-party notices](THIRD-PARTY-NOTICES.md)
