# BrainFuel

一个常驻置顶的桌面小组件，用于监控 **GLM Coding Plan** 的 5 小时滚动额度和每周额度。基于 **Avalonia 12 / .NET 10**，支持 Windows / macOS / Linux。

![C#](https://img.shields.io/badge/C%23-512BD4?logo=csharp&logoColor=white) [![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

[English](README.md) · **中文**

## 产品设计

BrainFuel 从 v0.4.0 开始明确拆开两个完全不同的概念：

- **额度数据**只属于主卡片。主卡片上的可见动作明确叫 **“刷新额度”**。
- **应用级操作**统一放在右上角 **“⋯”应用菜单**与设置中：设置、检查软件更新、关于、隐藏、退出。

这样主卡片只承担一个任务：看一眼额度，然后继续工作。

## 功能特性

- **聚焦额度的主卡片** — 周额度 / 5 小时额度双环、百分比、重置时间、数据新鲜度
- **明确的额度刷新** — 手动“刷新额度” + 可配置自动刷新
- **结构化设置** — 常规 / 账户 / 通知 / 软件四个页面，不再是一条很长的表单
- **首次运行引导** — 首次启动直接进入“账户”页，提供取 Key 链接并在 Key 发生变化时验证
- **Windows 自动软件更新** — 安装版后台检查 GitHub Releases，优先使用 Velopack delta 小包
- **清晰的版本信息** — 设置、关于窗口、主卡片应用菜单和托盘都能看到版本
- **可验证发布** — 正式 Release 提供 `SHA256SUMS` 与 GitHub Artifact Attestation
- **浅色 / 深色 / 跟随系统主题** — 支持卡片透明度
- **中英双语界面**
- **开机自启** — Windows / macOS / Linux
- **额度通知** — 可配置耗尽阈值
- **可拖动、多屏安全** — 记住位置，显示器变化后自动拉回可见区域
- **系统托盘** — 隐藏卡片后额度轮询和通知继续工作

## 工作原理

BrainFuel 使用你的 Coding Plan API Key 调用 GLM Coding Plan 监控接口：

`GET /api/monitor/usage/quota/limit`

设置文件位于 `%APPDATA%\BrainFuel\`（Windows）、`~/.config/BrainFuel/`（Linux）或 `~/Library/Application Support/BrainFuel/`（macOS）。Release 构建不会持续把原始额度响应写入磁盘；原始响应日志只用于 Debug 排查。

## 软件更新

**Windows 安装版**使用 Velopack。启动约 15 秒后检查一次软件更新，此后每 6 小时检查一次。发现新版本时后台下载；可用时优先下载 delta 差分包，否则回退完整包。

手动软件更新入口位于：

- 主卡片右上角 **⋯ → 检查软件更新…**
- 系统托盘菜单
- **设置 → 软件**

它不会再和“刷新额度”并排出现，因此不会混淆“刷新额度数据”和“升级 BrainFuel”。

便携版刻意不做自更新，需要从 Releases 下载最新版 ZIP 后手动替换。

## Windows 安装

### 推荐：一键 Setup

普通用户优先下载：

`BrainFuel-win-Setup.exe`

Velopack 的 Setup 本身就是一键安装设计，不会加入冗长的“下一步、下一步”向导。v0.4.0 起我们保留这种速度，但加入 **BrainFuel 品牌安装 Splash**，不再完全是默认的普通安装体验。

### 可选：MSI

v0.4.0 起同时发布：

`BrainFuel-win-Setup.msi`

MSI 面向更喜欢传统 Windows Installer 流程的用户或管理员，包含欢迎页、MIT License 和安装完成说明，默认按当前用户安装。安装完成后，应用内自动更新方式与 Setup.exe 安装版一致。

### SmartScreen 与文件验证

BrainFuel 是免费开源项目，目前**不购买商业 Authenticode 代码签名证书**，因此 Windows 仍可能显示 SmartScreen 或“未知发布者”。

每个正式 Release 都包含 `SHA256SUMS`：

```powershell
Get-FileHash .\BrainFuel-win-Setup.exe -Algorithm SHA256
Get-Content .\SHA256SUMS
```

如果安装了 GitHub CLI，还可以验证 GitHub Actions / Sigstore 构建来源：

```powershell
gh attestation verify .\BrainFuel-win-Setup.exe --repo turinglambdaai/brainfuel
```

MSI、Portable ZIP 与 Velopack 包同样可以验证。Artifact Attestation 用于证明构建来源，**不是 Authenticode 签名，不会消除 SmartScreen**。更多说明见 [`SECURITY.md`](SECURITY.md)。

## 官方发布文件

只建议从本仓库 [Releases](https://github.com/turinglambdaai/brainfuel/releases) 下载。

| 文件 | 用途 |
|---|---|
| `BrainFuel-win-Setup.exe` | **Windows 普通用户推荐** |
| `BrainFuel-win-Setup.msi` | 传统 / 管理型 Windows 安装 |
| `BrainFuel-windows-x64.zip` | Windows 便携版 |
| `BrainFuel-macos-arm64.zip` | macOS 便携版 |
| `BrainFuel-linux-x64.zip` | Linux 便携版 |

便携包都是自包含版本，目标机器无需另外安装 .NET。目前 macOS 与 Linux 仍以便携包为主。

## 从源码运行

```bash
git clone https://github.com/turinglambdaai/brainfuel.git
cd brainfuel
dotnet run
```

首次启动时进入 **设置 → 账户**，粘贴 GLM Coding Plan Key。

## 发布工程

Windows 本地打包：

```powershell
./installer/build-velopack.ps1
./installer/build-velopack.ps1 -DownloadPrevious
```

打包脚本会生成：品牌化一键 Setup、可选 MSI、full package、可用时的 delta package，以及 Velopack 更新 feed。

正式 GitHub Actions Release 还会：

- 构建 Windows / macOS / Linux 便携包；
- 强制检查 Setup、MSI、full package、feed，以及正式发布时的 delta；
- 为构建产物生成 GitHub Artifact Attestation；
- 生成并反向校验 `SHA256SUMS`；
- 只有全部通过后才创建正式 Release。

## 项目结构

```text
brainfuel/
├── App.axaml(.cs)                 # 应用生命周期 / 托盘 / 后台服务
├── MainWindow.axaml(.cs)          # 聚焦额度的主卡片 + 应用菜单
├── SettingsWindow.axaml(.cs)      # 常规 / 账户 / 通知 / 软件
├── AboutWindow.axaml(.cs)         # 版本 / 安装类型 / 项目链接
├── NotificationWindow.axaml(.cs)  # 桌面通知
├── Controls/UsageRing.cs
├── Services/
│   ├── GlmUsageClient.cs
│   ├── UpdateService.cs
│   ├── SettingsService.cs
│   ├── AutoStartService.cs
│   ├── SingleInstanceActivation.cs
│   ├── Strings.cs
│   └── UsageModels.cs
├── installer/
│   ├── build-velopack.ps1         # 品牌 Setup + MSI + 更新 feed
│   ├── welcome.md
│   └── conclusion.md
├── .github/workflows/
│   ├── ci.yml
│   └── release.yml
├── SECURITY.md
└── BrainFuel.csproj
```

## 许可证

基于 [MIT 许可证](LICENSE) 授权。
