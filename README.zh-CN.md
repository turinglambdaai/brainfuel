# brainfuel

一个常驻置顶的桌面小组件，用于监控你的 **GLM Coding Plan** 额度——包括 5 小时滚动窗口和每周额度，避免在开发过程中突然触发限流。基于 **Avalonia 12** / .NET 10 构建，支持跨平台（Windows / macOS / Linux）。

![C#](https://img.shields.io/badge/C%23-512BD4?logo=csharp&logoColor=white) [![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

[English](README.md) · **中文**

## 功能特性

- **嵌套额度环形图** — 外环 = 每周额度，内环 = 5 小时滚动窗口，带动画
- **自动刷新** — 可点击的刷新按钮，并每隔几分钟自动刷新
- **浅色 / 深色 / 跟随系统主题** — Anthropic 风格配色，附带卡片透明度滑块，不遮挡桌面
- **中英双语界面** — 中文 / English 切换
- **开机自启** — 可选（Windows 注册表 / macOS LaunchAgent / Linux 自启）
- **额度通知** — 额度达到耗尽阈值时可选桌面提醒（默认已用 80%，可配置）
- **可拖动卡片** — 右键菜单可刷新 / 设置 / 退出；拖动可移动（位置会被记住）

## 工作原理

使用你的 Coding Plan API Key 调用 GLM Coding Plan 监控接口（`GET /api/monitor/usage/quota/limit`）读取额度——与官方 `glm-plan-usage` 插件的调用一致。两个嵌套环形：外环 = 每周额度，内环 = 5 小时窗口。可点击刷新按钮；每隔几分钟自动刷新。

设置和 API Key 存放位置：`%APPDATA%\BrainFuel\`（Windows）/ `~/.config/BrainFuel/`（Linux）/ `~/Library/Application Support/BrainFuel/`（macOS）。

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

**方案 A — 下载预编译可执行文件**，见 [Releases](https://github.com/turinglambdaai/brainfuel/releases)。`release` 工作流为 `win-x64`、`osx-arm64`、`linux-x64` 构建自包含单文件可执行程序——目标机器无需安装 .NET。（在 [Actions 标签页](https://github.com/turinglambdaai/brainfuel/actions) 手动运行也会生成可下载的构建产物。）

**方案 B — 本地构建：**

```powershell
./publish.ps1                 # win-x64（默认）
./publish.ps1 osx-arm64       # macOS Apple Silicon
./publish.ps1 linux-x64       # Linux
```

输出位于 `publish/<rid>/`。

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
│   ├── SettingsService.cs    # 设置 / API Key 持久化
│   ├── AutoStartService.cs   # 开机自启（Win/macOS/Linux）
│   ├── SingleInstanceActivation.cs  # 单实例守卫
│   ├── Strings.cs            # 本地化字符串
│   └── UsageModels.cs        # 额度数据模型
├── ViewModels/
│   └── MainViewModel.cs      # MVVM 视图模型
├── publish.ps1               # 本地自包含构建脚本
└── BrainFuel.csproj          # 工程文件
```

## 许可证

基于 [MIT 许可证](LICENSE) 授权。
