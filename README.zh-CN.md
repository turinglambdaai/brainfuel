# BrainFuel

一个常驻置顶的桌面小组件，用于监控你的 **GLM Coding Plan** 额度——包括 5 小时滚动窗口和每周额度，避免在开发过程中突然触发限流。基于 **Avalonia 12** / .NET 10 构建，支持 Windows / macOS / Linux。

![C#](https://img.shields.io/badge/C%23-512BD4?logo=csharp&logoColor=white) [![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

[English](README.md) · **中文**

## 功能特性

- **首次运行引导** — 首次启动自动打开设置窗：带快速上手说明、控制台取 Key 链接，保存时自动验证 Key（离线/受限网络可选「仍然保存」）
- **嵌套额度环形图** — 外环 = 每周额度，内环 = 5 小时滚动窗口，带动画
- **自动刷新** — 可点击的刷新按钮，并每隔几分钟自动刷新
- **Windows 自动更新** — 安装版启动后自动检查 GitHub Releases；优先下载 Velopack 差分包，差分不可用时自动回退完整包；也可从卡片右键菜单或设置页手动检查并立即安装重启
- **可验证发布** — v0.3.5 起每个正式 Release 自动附带 SHA-256 清单与 GitHub Artifact Attestation，可验证二进制确实来自本仓库 GitHub Actions
- **浅色 / 深色 / 跟随系统主题** — Anthropic 风格配色，附带卡片透明度滑块，不遮挡桌面
- **中英双语界面** — 中文 / English 切换
- **开机自启** — 可选（Windows 注册表 / macOS LaunchAgent / Linux 自启）
- **额度通知** — 额度达到耗尽阈值时可选桌面提醒（默认已用 80%，可配置）
- **可拖动卡片** — 右键菜单可刷新 / 设置 / 检查更新 / 隐藏到托盘 / 退出；拖动可移动（位置会被记住）
- **系统托盘** — 隐藏到托盘后台继续刷新与通知，托盘图标（或再次启动 exe）随时调出；托盘菜单「退出」才会真正退出
- **多屏自适应** — 拔插显示器时若卡片落在已断开的屏幕外，会自动拉回主屏可见区域

## 工作原理

使用你的 Coding Plan API Key 调用 GLM Coding Plan 监控接口（`GET /api/monitor/usage/quota/limit`）读取额度——与官方 `glm-plan-usage` 插件的调用一致。两个嵌套环形：外环 = 每周额度，内环 = 5 小时窗口。可点击刷新按钮；每隔几分钟自动刷新。

设置和 API Key 存放位置：`%APPDATA%\BrainFuel\`（Windows）/ `~/.config/BrainFuel/`（Linux）/ `~/Library/Application Support/BrainFuel/`（macOS）。Release 构建不会把原始额度响应持续写入磁盘；原始响应文件只用于 Debug 构建排查。

## 自动更新

Windows 安装版使用 Velopack：启动约 15 秒后检查一次更新，此后每 6 小时检查一次。发现新版本后在后台下载；存在可用 delta 时只下载版本之间的差异，不适合使用 delta 时自动回退 full package。后台下载完成的更新会在下次正常启动时应用；用户主动点击 **检查更新** 时，则会下载、安装并自动重启到新版本。

便携版刻意不做自更新。设置页会明确显示当前是 **Windows 安装版** 还是 **便携版**；便携版会直接提供 GitHub Releases 下载入口，更新方式是下载最新版 ZIP 后手动替换旧程序文件。

### 从 v0.2.x 及更早版本迁移

旧版本使用 Inno Setup，并不包含 Velopack 更新引擎，因此 **无法从旧版本自动升级到首个 Velopack 版本**。这是唯一一次需要手工迁移：

1. 退出旧版 BrainFuel。
2. 卸载旧版（用户数据 `%APPDATA%\BrainFuel` 会保留）。
3. 从 Releases 下载并安装新的 `BrainFuel-win-Setup.exe`。

之后的 Windows 安装版更新由应用自行完成。便携 ZIP 不属于安装版，因此继续采用手动替换；当前 macOS / Linux 发布也仍为便携包，暂不启用应用内更新。

## 环境要求

| 依赖 | 用途 / 版本 |
|------|-------------|
| [.NET 10 SDK](https://dotnet.microsoft.com/) | 运行时 / 构建目标 |
| [Avalonia 12](https://avaloniaui.net/) | 跨平台 UI 框架 |
| Windows / macOS / Linux | 支持的桌面平台 |

## 快速开始

```bash
git clone https://github.com/turinglambdaai/brainfuel.git
cd brainfuel
dotnet run
```

首次启动时，在设置对话框中粘贴你的 GLM Coding Plan Key。

> 如果 `dotnet build`/`restore` 无法访问 nuget.org（受限网络），可改为从本地包缓存还原：`dotnet restore --ignore-failed-sources`。

## 分发

只建议从本仓库的 [Releases](https://github.com/turinglambdaai/brainfuel/releases) 下载官方二进制。

### Windows 推荐下载

**除非你明确需要便携版，否则优先下载 `BrainFuel-win-Setup.exe`。** 安装版是面向普通用户的默认分发方式，支持后台自动检查更新、可用时只下载很小的 delta 增量包、手动一键检查/下载安装/重启、正常卸载，以及在设置页显示清晰的版本与安装类型。

### Windows SmartScreen 与文件验证

BrainFuel 是免费开源项目，目前**不购买商业 Authenticode 代码签名证书**。因此即使文件来自官方 Release，Windows 仍可能显示 SmartScreen、`未知发布者` 或信誉警告。这不等同于文件校验失败；如果你希望确认下载内容，可以使用从 **v0.3.5** 开始提供的两套免费验证机制。

每个正式 Release 都会附带 `SHA256SUMS`。在 PowerShell 中：

```powershell
Get-FileHash .\BrainFuel-win-Setup.exe -Algorithm SHA256
Get-Content .\SHA256SUMS
```

第一条命令得到的 SHA-256 必须和 `SHA256SUMS` 中 `BrainFuel-win-Setup.exe` 对应条目完全一致。

如果安装了 GitHub CLI，还可以验证由 GitHub Actions / Sigstore 生成的构建来源证明：

```powershell
gh attestation verify .\BrainFuel-win-Setup.exe --repo turinglambdaai/brainfuel
```

Portable ZIP 和 Velopack 包也可以用同样方式验证。Artifact Attestation 用于证明该文件摘要由本仓库的 GitHub Actions 工作流进行过证明，**它不是 Windows Authenticode 签名，因此不会消除 SmartScreen 提示**。更多安全说明见 [`SECURITY.md`](SECURITY.md)。

### 便携版 / 高级用途

以下 ZIP 继续保留，用于不方便安装或不希望写入系统安装信息的场景：

- `BrainFuel-windows-x64.zip`
- `BrainFuel-macos-arm64.zip`
- `BrainFuel-linux-x64.zip`

它们都是自包含版本，目标机器无需安装 .NET。便携版 **不会修改自身目录，也不会自动更新**；需要升级时，从 Releases 下载最新版 ZIP 并手动替换原文件即可。它更适合临时测试、无安装权限的公司电脑、U 盘/移动目录运行以及问题排查。

目前 macOS 和 Linux 仍只提供便携版。

本地构建：

```powershell
./publish.ps1                          # 便携单文件 exe（默认 win-x64）
./installer/build-velopack.ps1         # Windows 安装版 + 更新 feed
./installer/build-velopack.ps1 -DownloadPrevious  # 有上一版 feed 时生成 delta
```

仓库使用 `.config/dotnet-tools.json` 锁定与应用 SDK 相同版本的 `vpk`。正式发布时，GitHub Actions 会验证版本号、生成三平台便携包和 Windows Velopack 资产、生成/验证 delta、对构建产物生成 GitHub Artifact Attestation、生成并自校验 `SHA256SUMS`，全部成功后才创建 Release。`release.yml` 同时支持标签触发和带版本号的手动发布，不再需要一次性的版本专用工作流。

## 项目结构

```text
brainfuel/
├── App.axaml(.cs)                 # 应用生命周期 / 后台服务
├── Program.cs                     # 入口 / Velopack 启动钩子
├── MainWindow.axaml(.cs)          # 主组件窗口（额度环形图）
├── NotificationWindow.axaml(.cs)  # 桌面通知窗口
├── SettingsWindow.axaml(.cs)      # 设置 + API Key 对话框
├── Controls/
│   └── UsageRing.cs               # 可复用额度环形控件
├── Services/
│   ├── GlmUsageClient.cs          # GLM Coding Plan 额度 API 客户端
│   ├── UpdateService.cs           # 后台检查 / 下载应用更新
│   ├── SettingsService.cs         # 设置 / API Key 持久化
│   ├── AutoStartService.cs        # 开机自启（Win/macOS/Linux）
│   ├── SingleInstanceActivation.cs# 单实例守卫
│   ├── Strings.cs                 # 本地化字符串
│   └── UsageModels.cs             # 额度数据模型
├── installer/
│   ├── build-velopack.ps1         # Windows 安装版与差分更新打包
│   └── build-installer.ps1        # 旧 Inno Setup 打包（兼容/迁移参考）
├── .github/workflows/
│   ├── ci.yml                     # 三平台编译 + Windows 更新包烟测
│   └── release.yml                # 发布 + 校验和 + provenance
├── SECURITY.md                    # 二进制验证与安全报告说明
└── BrainFuel.csproj
```

## 许可证

基于 [MIT 许可证](LICENSE) 授权。
