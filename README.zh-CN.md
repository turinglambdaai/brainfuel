# BrainFuel

一个用于监控 **GLM Coding Plan** 5 小时滚动额度和每周额度的桌面小组件。基于 **Avalonia 12 / .NET 10**，支持 Windows / macOS / Linux。

![C#](https://img.shields.io/badge/C%23-512BD4?logo=csharp&logoColor=white) [![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

[English](README.md) · **中文**

## 产品设计

BrainFuel 明确拆开几个不同概念：

- **额度数据**只属于主卡片。主卡片上的可见动作明确叫 **“刷新额度”**。
- **三层信息架构** — L0：常驻卡片，看一眼即走（零交互）。L1：**双击卡片**打开详情面板（大号数字、燃速、预计耗尽、重置时间、套餐档位、手动刷新）。L2：⋯ 菜单与设置负责配置。没有主窗口，没有任务栏噪音。
- **应用级操作**统一放在右上角 **“⋯”应用菜单**与设置中：设置、检查软件更新、跨屏移动、关于、隐藏、退出。关闭卡片（X 按钮 / Alt+F4 / 系统菜单）同样只是“隐藏”：会弹一条气泡提示卡片去了托盘，只有显式**退出**才会结束进程——监控不会随窗口一起消失。
- **桌面行为由用户决定**。新安装不会强制压在所有应用上方；“置顶显示”是可选能力。

目标很简单：看一眼额度，然后继续工作。

## 功能特性

- **聚焦额度的主卡片** — 周额度 / 5 小时额度双环、百分比、重置时间、数据新鲜度
- **迷你模式** — ⋯ 菜单一键切换成一环迷你卡，自动追踪更接近耗尽的那个窗口
- **明确的额度刷新** — 手动“刷新额度” + 可配置自动刷新
- **API Key 安全存储** — Windows DPAPI、macOS Keychain、Linux Secret Service；旧版本明文 Key 可自动迁移
- **结构化设置** — 常规 / 账户 / 通知 / 软件四个页面
- **不打扰的桌面行为** — 新安装默认不强制置顶、启动不抢焦点；需要时可随时开启“置顶显示”
- **多屏感知定位** — 记住物理显示器和相对位置，处理 DPI / 分辨率 / 任务栏变化，并在外接屏消失后自动恢复
- **显式跨屏控制** — 应用菜单可直接“移到下一个屏幕”或“移到主屏幕”
- **首次运行引导** — 首次启动直接进入“账户”页，提供取 Key 链接并验证 Key
- **Windows 自动软件更新** — 安装版后台检查 GitHub Releases，优先使用 Velopack delta 小包
- **清晰的版本信息** — 设置、关于、主卡片菜单和托盘都能看到版本
- **可验证发布** — 正式 Release 提供 `SHA256SUMS` 与 GitHub Artifact Attestation
- **浅色 / 深色 / 跟随系统主题** — 支持卡片透明度
- **中英双语界面**
- **开机自启** — Windows / macOS / Linux
- **额度通知** — 可配置耗尽阈值
- **系统托盘** — 隐藏卡片（关闭 / Alt+F4 同样生效）后额度轮询和通知继续工作
- **可读的额度失败信息** — 卡片新鲜度行直接给出失败类别（Key 无效/平台不匹配、网络、代理、超时、无套餐等）；悬停卡片可见完整指引与日志路径
- **燃速智能** — 详情面板（双击卡片）可见各窗口消耗速度与预计耗尽时间；阈值通知会追加预测（「按当前燃速，约 2 小时后耗尽」），文案也更有性格
- **用量历史曲线** — 详情面板用本地滚动历史绘制 5 小时窗口（近 24 小时）与周额度（近 7 天）曲线，重置与消耗节奏一眼可见
- **告急视觉** — 环形、圆点与百分比在已用 75% 变琥珀、90% 变红，标准卡与迷你卡一致

## 桌面与多屏行为

v0.5 开始，BrainFuel 不再只保存一个脆弱的绝对 `(x, y)` 坐标，而是按显示器 **Working Area（可用工作区域）** 保存卡片的相对位置。

行为规则刻意设计得比较保守：

- **新安装**默认出现在主屏幕右上区域，并留出边距；
- 用户拖动后，记住卡片所在显示器以及相对位置；
- 如果卡片所在的外接屏被拔掉，自动迁到**主屏幕的相同相对位置**；
- 外接屏重新接入后，**不会突然把卡片又拉回去**，避免打断当前工作；
- 显示器重新排列、分辨率/横竖屏变化、DPI 缩放变化、Windows 任务栏或 macOS Dock 工作区变化时，会保证整张卡片仍处于可用区域；
- 右上角 **⋯ → 移到下一个屏幕 / 移到主屏幕** 提供明确的人工恢复入口；
- 新安装默认关闭 **“置顶显示”**，普通工作窗口可以自然覆盖 BrainFuel；从旧版升级的用户会保留原来的置顶习惯，直到自己修改设置。

BrainFuel 自动启动时还会使用“不主动激活窗口”的方式，避免登录后抢走当前输入焦点；只有用户从托盘主动调出或再次启动程序时，才会把卡片带到前面。

## API Key 安全存储

BrainFuel 使用你的 Coding Plan API Key 调用 GLM Coding Plan 监控接口：

`GET /api/monitor/usage/quota/limit`

从 **v0.5.0** 开始，API Key 正常情况下不再明文存放在 `settings.json`：

| 平台 | 首选安全存储 |
|---|---|
| Windows | 当前 Windows 用户的 DPAPI 保护 |
| macOS | 登录 Keychain |
| Linux | freedesktop Secret Service（通过 `secret-tool`） |

第一次从旧版本升级到 v0.5 时，如果 `settings.json` 里还有历史明文 Key，BrainFuel 会尝试自动迁入系统安全存储；成功后立即重写设置文件，删除明文 Key。

系统密钥环偶尔可能暂时不可访问。此时 BrainFuel 的策略是：

- 已经受保护的 Key **不会因为一次读取失败就降级写回明文**；
- 用户只修改主题、刷新间隔、置顶等普通设置时，不会重新写或删除未变化的 Key；
- 保留“已有受保护凭据”的状态，并在之后的额度刷新中自动重试；
- 系统密钥环恢复后，无需重新输入 Key 或重启 BrainFuel；
- 设置页会明确显示当前是“已安全保护 / 安全存储暂不可用 / 本地文件回退”中的哪一种状态。

如果用户在系统安全存储不可用时**新建或更换** Key，为了不把用户输入直接丢掉，BrainFuel 会回退到本地 `settings.json`。在 Linux/macOS 上，如果文件系统支持，会把权限限制为当前用户 `0600`，同时设置页会明确提示这个回退状态。完整安全模型见 [`SECURITY.md`](SECURITY.md)。

非敏感设置文件位于 `%APPDATA%\BrainFuel\`（Windows）、`~/.config/BrainFuel/`（Linux）或 `~/Library/Application Support/BrainFuel/`（macOS）。Release 构建不会持续把原始额度响应写入磁盘；原始响应日志只用于 Debug 排查。额度**失败记录**（类别 + 服务端消息，绝不含 Key 或原始报文）会滚动追加到 `settings.json` 同目录的 `brainfuel.log`，卡片失败悬停提示中会给出该路径，便于事后排查“填了 Key 却刷不出额度”。同目录的 `usage-history.json` 只保存已用百分比与时间戳（本地，保留 8 天），用于历史曲线。

## 软件更新

**Windows 安装版**使用 Velopack。启动约 15 秒后检查一次软件更新，此后每 6 小时检查一次。发现新版本时后台下载；可用时优先下载 delta 差分包，否则回退完整包。

手动软件更新入口位于：

- 主卡片右上角 **⋯ → 检查软件更新…**
- 系统托盘菜单
- **设置 → 软件**

它不会和“刷新额度”并排出现，因此不会混淆“刷新额度数据”和“升级 BrainFuel”。

便携版刻意不做自更新，需要从 Releases 下载最新版 ZIP 后手动替换。

## Windows 安装

### 推荐：一键 Setup

普通用户优先下载：

`BrainFuel-win-Setup.exe`

Velopack 的 Setup 本身就是一键安装设计，不加入冗长的“下一步、下一步”向导。v0.4.0 起保留这种速度，同时加入 **BrainFuel 品牌安装 Splash**。

### 可选：MSI

同时发布：

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

```bash
dotnet test BrainFuel.Tests/BrainFuel.Tests.csproj   # 失败分类回归测试（CI 也会跑）
```

## 发布工程

Windows 本地打包：

```powershell
./installer/build-velopack.ps1
./installer/build-velopack.ps1 -DownloadPrevious
```

正式 GitHub Actions Release 会：

- 构建 Windows / macOS / Linux 便携包；
- 强制检查 Setup、MSI、full package、feed，以及正式发布时的 delta；
- 为构建产物生成 GitHub Artifact Attestation；
- 生成并反向校验 `SHA256SUMS`；
- 只有全部通过后才创建正式 Release。

## 项目结构

```text
brainfuel/
├── App.axaml(.cs)                 # 应用生命周期 / 托盘 / 后台服务
├── MainWindow.axaml(.cs)          # 额度主卡片 + 桌面 / 应用菜单行为
├── SettingsWindow.axaml(.cs)      # 常规 / 账户 / 通知 / 软件
├── AboutWindow.axaml(.cs)         # 版本 / 安装类型 / 项目链接
├── NotificationWindow.axaml(.cs)  # 桌面通知
├── Controls/UsageRing.cs
├── Services/
│   ├── CredentialStore.cs         # DPAPI / Keychain / Linux Secret Service
│   ├── WindowPlacementService.cs  # 多屏相对定位与恢复
│   ├── GlmUsageClient.cs
│   ├── AppLog.cs                 # 滚动错误日志（不含 Key/原始报文）
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
├── BrainFuel.Tests/            # xUnit 测试（额度失败分类、重试语义）
├── SECURITY.md
└── BrainFuel.csproj
```

## 许可证

基于 [MIT 许可证](LICENSE) 授权。
