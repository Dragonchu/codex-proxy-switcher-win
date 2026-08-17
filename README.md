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

- 通过 Windows 包标识 API 查找 Microsoft Store/MSIX 版 Codex，并通过 `IApplicationActivationManager` 激活。
- 查找 `%LOCALAPPDATA%\Programs\Codex\Codex.exe` 形式的非打包安装。
- 启动前检查代理 TCP 端口是否可达。
- 对非打包版 Codex，仅向新进程传递 `HTTP_PROXY`、`HTTPS_PROXY`、`ALL_PROXY` 和 `NO_PROXY`。
- 如果 Codex 已运行，显示“Codex 正在运行。请退出 Codex 后再启动。”，不会结束、关闭或重启它。
- 不修改 Windows 全局代理、注册表、用户级环境变量或系统级环境变量。

## 已知限制

Windows 的文档化商店应用激活 API 不提供为目标应用指定环境变量的参数。应用通过 AppUserModelID 激活时，启动器无法保证 Store/MSIX 版 Codex 继承 `HTTP_PROXY`、`HTTPS_PROXY` 或 `ALL_PROXY`。因此，对商店版安装，本工具可以检查本地代理并正常激活 Codex，但不能可靠地实现“仅给该商店应用注入代理”。是否使用代理取决于 Codex 自身、Windows 网络设置及代理软件的接管方式。

本项目不会为了绕过该限制而临时修改用户环境变量、广播环境变化、直接执行受保护的 WindowsApps 可执行文件，或用 PowerShell 作为生产启动链路。若必须确保商店版 Codex 走代理，请使用代理软件提供的进程接管/TUN 功能，或使用 Codex 官方支持的代理配置（如未来提供）。

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

意外启动失败时，窗口显示稳定错误代码 `CPS-LAUNCH-001` 和完整异常堆栈。提交 GitHub Issue 时请复制两者，并补充 Windows 版本、Codex 安装来源及复现步骤。堆栈可能包含本机路径，请在公开提交前自行检查敏感信息。

## 贡献与许可证

欢迎提交 Issue 和 Pull Request。变更应保持界面与架构精简，使用文档化的 Windows API，不得加入修改全局代理、用户环境变量或强制管理 Codex 进程的行为。

本项目采用 [MIT License](LICENSE)。
