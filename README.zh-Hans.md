# opssh

[English](README.md) · **简体中文**

opssh 是面向 Windows x64 系统 OpenSSH 客户端的 SSH 代理。它使用 1Password 桌面应用中的 SSH 密钥在内存中签名。Windows 通知会显示应用、PID 和密钥指纹，供你决定是否批准签名。

- 可批准单次签名，也可允许所示应用复用该密钥。
- 可选择哪些 1Password 保险库和 SSH Key 条目中的密钥可供 SSH 使用。
- 可在需要的 PowerShell 会话中选择 opssh，同时运行 Windows 系统代理。

<a name="构建和使用"></a>
<a name="从正式发行版安装"></a>

## 安装

正式发行版发布后，可下载 Windows x64 MSI、离线 EXE 安装器或便携 ZIP。MSI 和 EXE 按当前用户安装，安装后可从开始菜单打开 **opssh**。使用 ZIP 时，解压后运行其中的 `opssh-tray.exe`。安装器和 opssh 二进制文件均未签名。[GitHub 发行版](https://github.com/Cirnouo/opssh/releases)

首个正式发行版发布前，可按[源码构建步骤](CONTRIBUTING.md#verify-and-submit)运行 opssh。

如果 opssh 正在运行，请在安装、修复、升级或卸载前，从托盘菜单选择 **退出 opssh**。安装位置和卸载时保留的数据见[安装与维护步骤](docs/user-guide/README.zh-Hans.md#install)。

## 快速开始

1. 登录 1Password 桌面应用并安装官方 CLI。在 1Password 中启用 CLI 和 SDK 集成。
2. 从托盘打开 opssh，选择账户并完成 1Password 验证。在 **1Password** 页允许需要的保险库，再到 **SSH 密钥** 页选择 **发现 SSH 密钥**。
3. 在 **设置 → SSH 集成** 中复制 PowerShell 命令，在需要使用 opssh 的会话执行。运行 `ssh-add -L`，确认公钥后再批准签名请求。


## 使用指南

[阅读简体中文使用指南](docs/user-guide/README.zh-Hans.md)，了解密钥范围、批准、缓存设置和故障处理。

<a name="语言和主题"></a>
<a name="更新"></a>

[切换或恢复 SSH 代理](docs/user-guide/README.zh-Hans.md#select-agent) · [外观与更新](docs/user-guide/README.zh-Hans.md#appearance-and-updates) · [故障处理](docs/user-guide/README.zh-Hans.md#troubleshooting)

## 常见问题

**重启 opssh 后需要重新连接账户吗？**

opssh 会恢复已保存的账户和公钥索引。下次从 1Password 读取数据时，桌面应用可能会要求重新授权。

**其他 shell 可以使用不同的代理吗？**

可以。显示的 PowerShell 命令只为当前 shell 及其启动的进程切换代理。如果 SSH 配置中设有 `IdentityAgent`，该设置可能覆盖环境变量。

**清除私钥会撤销授权吗？**

在 **SSH 密钥** 页分别使用 **清除已加载私钥** 和 **撤销全部授权**。桌面访问权限由 1Password 管理。


## 资源与支持

[1Password 桌面应用 SDK 集成](https://www.1password.dev/sdks/desktop-app-integrations) · [Windows 代理行为与安全边界](docs/specs/windows-agent.md) · [离线性能实测](docs/references/windows-offline-performance.md) · [更新日志](CHANGELOG.md)

[使用支持](SUPPORT.md) · [私下报告漏洞](SECURITY.md) · [构建、验证与贡献](CONTRIBUTING.md)

## 许可证

[Apache License 2.0](LICENSE) · [第三方许可声明](THIRD-PARTY-NOTICES.md)
