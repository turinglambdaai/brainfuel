# BrainFuel

一个常驻置顶的桌面小组件，用于监控你的 **GLM Coding Plan** 额度——包括 5 小时滚动窗口和每周额度，避免在开发过程中突然触发限流。基于 **Avalonia 12** / .NET 10 构建，支持跨平台（Windows / macOS / Linux）。

![C#](https://img.shields.io/badge/C%23-512BD4?logo=csharp&logoColor=white) [![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

[English](README.md) · **中文**

## 功能特性

- **首次运行引导** — 首次启动自动打开设置窗：带快速上手说明、控制台取 Key 链接，保存时自动验证 Key（离线/受限网络可选「仍然保存」）
- **嵌套额度环形图** — 外环 = 每周额度，内环 = 5 小时滚动窗口，带动画
- **自动刷新** — 可点击的刷新按钮，并每隔几分钟自动刷新
- **浅色 / 深色 / 跟随系统主题** — Anthropic 风格配色，附带卡片透明度滑块，不遮挡桌面
- **中英双语界面** — 中文 / English 切换
- **开机自启** — 可选（Windows 注册表 / macOS LaunchAgent / Linux 自启）
- **额度通知** — 额度达到耗尽阈值时可选桌面提醒（默认已用 80%，可配置）
- **可拖动卡片** — 右键菜单可刷新 / 设置 / 隐藏到托盘 / 退出；拖动可移动（位置会被记住）
- **托盘常驻生命周期** — 点关闭（X 按钮 / Alt+F4 / 系统菜单）只是收起到托盘：刷新与通知照常运行，并弹一条气泡告诉你它去了哪。点击托盘图标（或再次启动 exe）随时调回卡片；只有托盘/卡片菜单的「退出」才会真正结束进程
- **可读的失败信息** — Key 被拒或网络不可达时，卡片刷新行直接给出真实原因（「刷新失败 · HTTP 401」、服务端原始消息等）；悬停卡片可见可能原因与日志路径
- **多屏自适应** — 拔插显示器时若卡片落在已断开的屏幕外，会自动拉回主屏可见区域

## 工作原理

使用你的 Coding Plan API Key 调用 GLM Coding Plan 监控接口（`GET /api/monitor/usage/quota/limit`）读取额度——与官方 `glm-plan-usage` 插件的调用一致。两个嵌套环形：外环 = 每周额度，内环 = 5 小时窗口。可点击刷新按钮；每隔几分钟自动刷新。

设置和 API Key 存放位置：`%APPDATA%\BrainFuel\`（Windows）/ `~/.config/BrainFuel/`（Linux）/ `~/Library/Application Support/BrainFuel/`（macOS）。

## 故障排查

**填了 Key 但刷不出额度。** 看卡片的刷新行——它直接写明失败原因（「刷新失败 · HTTP 401」「刷新失败 · token expired or incorrect」、超时等），悬停卡片还有常见原因提示。两个最常见的坑：

- **Key 与平台不匹配** — 智谱国内 Key 选成了「Z.ai 国际」（或反过来）会被网关拒绝。两个网关对无效 Key 都返回 HTTP 200 + 错误体，所以 0.2.1 之前的版本看起来「成功但为空」；0.2.1 起会明确报失败。
- **套餐不含 Coding Plan 额度** — 账号没有这类额度时接口不返回 token 限额，卡片会明确说明，而不是一直显示 `--`。

每次失败都会追加到 `settings.json` 同目录的 `brainfuel.log`（路径见卡片悬停提示），原始 API 响应保存在同目录 `quota-debug.json`。

## 环境要求

| 依赖 | 用途 / 版本 |
|------|-------------|
| [.NET 10 SDK](https://dotnet.microsoft.com/) | 运行时 / 构建目标 |
| [Avalonia 12](https://avaloniaui.net/) | 跨平台 UI 框架 |
| Windows / macOS / Linux | 支持的桌面平台 |

## 快速开始

### 1. 克隆仓库

```bash
git clone https://github.com/turinglambdaai/brainfuel.git
cd brainfuel
```

### 2. 运行

```bash
dotnet run
```

首次启动时，在设置对话框中粘贴你的 GLM Coding Plan Key。

> 如果 `dotnet build`/`restore` 无法访问 nuget.org（受限网络），可改为从本地包缓存还原：
> ```bash
> dotnet restore --ignore-failed-sources
> ```

## 分发

**方案 A — 从 [Releases](https://github.com/turinglambdaai/brainfuel/releases) 下载：**

- **Windows 安装包** — `BrainFuel-Setup-<版本号>.exe`：per-user 安装（无需管理员/UAC），装到 `%LOCALAPPDATA%\Programs\BrainFuel`，带开始菜单快捷方式、可选桌面快捷方式与开机自启，卸载干净（用户数据 `%APPDATA%\BrainFuel` 保留）。
- **便携版** — `win-x64`、`osx-arm64`、`linux-x64` 自包含单文件 zip，目标机器无需安装 .NET。

（在 [Actions 标签页](https://github.com/turinglambdaai/brainfuel/actions) 手动运行也会生成可下载的构建产物。）

**方案 B — 本地构建：**

```powershell
./publish.ps1                          # 便携单文件 exe（默认 win-x64）
./installer/build-installer.ps1        # Windows：发布 + 编译安装包
```

`build-installer.ps1` 从 `BrainFuel.csproj` 读取版本号，发布 `win-x64` 后编译 `installer/BrainFuel.iss`。脚本会依次在 PATH、Program Files、`.tools\` 下查找 Inno Setup，找不到时自动下载一份私有副本（无需管理员）。输出：`dist/BrainFuel-Setup-<版本号>.exe`。

## 项目结构

```
brainfuel/
├── App.axaml(.cs)            # 应用定义 / DI 容器
├── Program.cs                # 入口
├── MainWindow.axaml(.cs)     # 主组件窗口（额度环形图）
├── NotificationWindow.axaml(.cs)  # 桌面通知窗口
├── SettingsWindow.axaml(.cs) # 设置 + API Key 对话框
├── Controls/
│   └── UsageRing.cs          # 可复用额度环形控件
├── Services/
│   ├── GlmUsageClient.cs     # GLM Coding Plan 额度 API 客户端
│   ├── AppLog.cs             # 滚动错误日志（与设置同目录）
│   ├── SettingsService.cs    # 设置 / API Key 持久化
│   ├── AutoStartService.cs   # 开机自启（Win/macOS/Linux）
│   ├── SingleInstanceActivation.cs  # 单实例守卫
│   ├── Strings.cs            # 本地化字符串
│   └── UsageModels.cs        # 额度数据模型
├── ViewModels/
│   └── MainViewModel.cs      # MVVM 视图模型
├── installer/                # Inno Setup 脚本 + Windows 打包脚本
├── publish.ps1               # 本地自包含构建脚本
└── BrainFuel.csproj          # 工程文件
```

## 许可证

基于 [MIT 许可证](LICENSE) 授权。
