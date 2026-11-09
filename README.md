# opssh

opssh 是面向 Windows x64 系统 OpenSSH 的 SSH agent。Go 核心按需通过官方 1Password Go SDK 从已登录的桌面应用取得选定密钥，并在内存中签名。C# WinUI 3 托盘常驻，显示进程链和密钥指纹，并管理具体请求的批准。

- 可以仅批准本次请求、直接客户端进程，或一个宿主实例及其后代。宿主实例以 Windows PID 和创建时间区分。
- 私钥默认缓存固定 5 分钟，宿主批准默认缓存固定 10 分钟。两类缓存可独立配置。
- 连接 1Password 后自动建立公钥索引。默认包含该账户可访问密码库中的有效 SSH Key 条目，范围可精确到密码库和条目。私钥、签名和批准由 Go 核心管理。
- 使用每登录会话的专用管道，可以与现有系统 agent 共存。

当前版本的离线验收使用生成密钥和 mock 存储。生产版使用真实 Desktop SDK 适配器，实际 1Password 条目读取须按[单独的联调步骤](docs/references/1password-desktop-validation.md)验证。

## 构建和使用

安装 Go 1.27.3、.NET 10 SDK 10.0.403 和 PowerShell 7。在 Windows x64 的检出中执行：

```powershell
./tools/Test-Windows.ps1
```

脚本输出已检查的自包含 `release` 目录。启动其中的 `opssh-tray.exe`，通过托盘图标打开设置。在 **1Password & keys** 中填写 1Password 桌面应用左侧显示的账户名或账户 ID，并点击 **Connect**。账户需已加入 1Password 桌面应用，并启用 **Settings → Developer → Integrate with 1Password SDKs**。身份验证和读取授权在 1Password 官方窗口完成。

连接后，opssh 自动读取范围内 SSH Key 条目的公钥。可设置账户默认范围，并通过密码库和 SSH Key 条目名称调整范围。条目规则优先于密码库规则，未设规则时继承上一级。新配置默认包含全部，旧版手工登记的配置保留原有条目范围。

在要使用 opssh 的 PowerShell 会话里，先记录当前 `$env:SSH_AUTH_SOCK`，然后执行托盘 **Show PowerShell command** 中显示的命令。用系统 OpenSSH 的 `ssh-add -L` 查看公钥。如果当前 SSH 配置设置了 `IdentityAgent`，该设置可能覆盖环境变量选择。回退时关闭这个 shell，或恢复之前的变量值。切换命令只影响当前进程及其子进程。

关闭设置窗口会释放窗口并返回托盘。托盘菜单中的 **Quit opssh** 会退出两个进程。锁定 opssh 会清除本地签名批准及私钥缓存。在 1Password 中新增、轮换、归档或删除条目后，请在托盘中刷新公钥索引。

[行为与安全边界](docs/specs/windows-agent.md) · [开发和验证](CONTRIBUTING.md) · [离线性能实测](docs/references/windows-offline-performance.md)
