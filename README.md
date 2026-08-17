# Codex Proxy Switcher for Windows

一个极简的 Windows 桌面启动器：检查本地代理与 Codex 是否可用，并在条件满足时启动 Codex。项目已使用 C#、.NET 10 LTS 和 WPF 重写。

## 要求

- Windows 10 2004（build 19041）或更高版本，x64
- Windows 版 Codex
- 正在监听的本地 HTTP 或 SOCKS5 代理
- 从源码运行时需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## 配置和使用

首次启动时输入完整代理地址，例如 `http://127.0.0.1:7890` 或 `socks5://localhost:7890`。仅接受回环地址，配置保存在：

```text
%LOCALAPPDATA%\CodexProxySwitcher\settings.json
```

启动器打开后会依次读取配置、查找 Codex、检查 Codex 是否已运行，并尝试连接代理端口。准备就绪时只有一个主操作“启动 Codex”。代理地址可通过窗口底部的“代理设置”修改。

## 支持的行为

- 通过 Windows 包标识 API 查找 Microsoft Store/MSIX 版 Codex，并使用兼容注入流程通过 `IApplicationActivationManager` 激活。
- 查找 `%LOCALAPPDATA%\Programs\Codex\Codex.exe` 形式的非打包安装。
- Store/MSIX 查询、包清单解析和非打包回退彼此隔离；任一路径失败都会继续尝试下一路径。全部失败时正常显示“未找到 Codex”，不会让初始界面因发现异常而崩溃。
- 启动前检查代理 TCP 端口是否可达。
- Store/MSIX 兼容流程由跨进程命名互斥锁串行化：再次检查 Codex 与代理，快照当前进程和当前用户的大小写代理变量，临时写入、广播环境变化、激活并确认进程出现；成功后只保留固定 1.5 秒启动租约，随后恢复快照并再次广播。激活失败、取消或超时也会在 `finally` 路径恢复。
- 对非打包版 Codex，仅向新进程的 `ProcessStartInfo` 传递大小写 `HTTP_PROXY`、`HTTPS_PROXY`、`ALL_PROXY` 和 `NO_PROXY`，不写用户级环境。
- 如果 Codex 已运行，显示“Codex 正在运行。请退出 Codex 后再启动。”，不会结束、关闭或重启它。
- 不修改 Windows 全局代理或系统级环境变量，不直接读写注册表，不使用 PowerShell 作为生产启动路径。

## 已知限制

Windows 的文档化商店应用激活 API 不提供为目标应用指定环境变量的参数。本工具采用的是兼容方案：在很短的激活窗口内，通过标准环境变量 API 临时修改当前用户变量并广播，再激活 Store/MSIX 应用。这不是 Windows 官方提供的应用级隔离注入，不能保证 Codex 一定读取这些变量，也无法阻止同一用户的其他新进程在租约期间看到临时值。

正常执行中的成功、激活失败、取消和超时都会恢复所有变量的原值；恢复会逐项尝试，即使某一项失败也继续处理其余项。但是进程被强制终止、系统关机或断电时，`finally` 无法运行，因此无法作绝对恢复保证。启动器自身绝不会结束或强杀 Codex。若必须确保 Store/MSIX 版 Codex 走代理，请使用代理软件提供的进程接管/TUN 功能，或使用 Codex 官方支持的代理配置（如未来提供）。

端口可达只表示本地服务正在监听，不代表代理能访问 OpenAI，也不验证代理协议、认证、TLS 或账号状态。

## 构建和运行

```powershell
dotnet build .\src\CodexProxySwitcher.csproj -c Release
dotnet run --project .\src\CodexProxySwitcher.csproj
```

发布依赖本机 .NET Desktop Runtime 的单文件版本：

```powershell
.\build.ps1
```

发布包含 .NET Runtime 的自包含版本：

```powershell
.\build.ps1 -SelfContained
```

产物位于 `dist\CodexProxySwitcher.exe`。

## 错误报告

用户点击“启动 Codex”后若发生无法恢复的启动错误，窗口显示稳定错误代码 `CPS-LAUNCH-001` 和完整异常堆栈。安装发现阶段的查询或解析失败会被隔离并降级为“未找到 Codex”，不会显示启动错误堆栈。提交 GitHub Issue 时请复制启动错误代码和堆栈，并补充 Windows 版本、Codex 安装来源及复现步骤。堆栈可能包含本机路径，请在公开提交前自行检查敏感信息。

## 贡献与许可证

欢迎提交 Issue 和 Pull Request。变更应保持界面与架构精简，使用文档化的 Windows API；不得修改全局代理、直接操作注册表或强制管理 Codex 进程，兼容环境租约必须保持可恢复且时间有界。

本项目采用 [MIT License](LICENSE)。
