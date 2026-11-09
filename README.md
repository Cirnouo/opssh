# opssh

opssh 是面向 Windows x64 系统 OpenSSH 的 SSH agent。Go 核心按需通过官方 1Password Go SDK 从已登录的桌面应用取得选定密钥，并在内存中签名。C# WinUI 3 托盘常驻，显示进程链和密钥指纹，并管理具体请求的批准。

- 可以仅批准本次请求、直接客户端进程，或一个宿主实例及其后代。宿主实例以 Windows PID 和创建时间区分。
- 私钥默认缓存固定 5 分钟，宿主批准默认缓存固定 10 分钟。两类缓存可独立配置。
- 公钥枚举使用本地索引。私钥、签名和批准由 Go 核心管理。
- 使用每登录会话的专用管道，可以与现有系统 agent 共存。

当前版本的离线验收使用生成密钥和 mock 存储。生产版使用真实 Desktop SDK 适配器，实际 1Password 条目读取须按[单独的联调步骤](docs/references/1password-desktop-validation.md)验证。

## 构建和使用

安装 Go 1.27.3、.NET 10 SDK 10.0.403 和 PowerShell 7。在 Windows x64 的检出中执行：

```powershell
./tools/Test-Windows.ps1
```

脚本输出已检查的自包含 `release` 目录。启动其中的 `opssh-tray.exe`，通过托盘图标打开设置。配置 1Password 账户名或账户 ID，然后输入 SSH Key 条目的密码库 ID 和条目 ID。添加和刷新时读取选定条目的公钥。托盘会显示读取失败、已加载和锁定等状态。

在要使用 opssh 的 PowerShell 会话里，先记录当前 `$env:SSH_AUTH_SOCK`，然后执行托盘 **Show PowerShell command** 中显示的命令。用系统 OpenSSH 的 `ssh-add -L` 查看公钥。如果当前 SSH 配置设置了 `IdentityAgent`，该设置可能覆盖环境变量选择。回退时关闭这个 shell，或恢复之前的变量值。切换命令只影响当前进程及其子进程。

关闭设置窗口会返回托盘。托盘菜单中的 **Quit opssh** 会退出两个进程。锁定 opssh 会清除本地签名批准及私钥缓存。1Password 中轮换或删除条目后，请在托盘中刷新它。

[行为与安全边界](docs/specs/windows-agent.md) · [开发和验证](CONTRIBUTING.md) · [离线性能实测](docs/references/windows-offline-performance.md)
