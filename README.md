# BrainFuel

A tiny always-on-top desktop widget that monitors your **GLM Coding Plan** quota — the 5-hour rolling window and the weekly allowance — so you don't get blindsided by a rate limit mid-session. Built with **Avalonia 12** / .NET 10. Cross-platform (Windows / macOS / Linux).

![C#](https://img.shields.io/badge/C%23-512BD4?logo=csharp&logoColor=white) [![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

**English** · [中文](README.zh-CN.md)

## Features

- **Guided first run** — the settings dialog opens automatically on first launch with a quick-start note, a console link to grab your GLM Coding Plan key, and on-save key validation (with a "save anyway" escape hatch for offline/restricted networks)
- **Nested quota rings** — outer ring = weekly allowance, inner ring = 5-hour rolling window, animated
- **Auto-refresh** — clickable refresh button plus automatic refresh every few minutes
- **Light / Dark / System theme** — Anthropic-style palette, with a card opacity slider so it doesn't block your desktop
- **Bilingual UI** — Chinese / English toggle
- **Start-on-login** — optional autostart (Windows registry / macOS LaunchAgent / Linux autostart)
- **Quota notifications** — optional desktop notification when a quota crosses an exhaustion threshold (default 80% used, configurable)
- **Movable card** — right-click for refresh / settings / hide / quit; drag to move (position remembered)
- **Tray-resident lifecycle** — closing the card (X button / Alt+F4 / system menu) just hides it to the tray: polling and notifications keep running, and a brief toast points you at the tray icon. Bring the widget back by clicking the tray icon (or by launching the exe again). Only the tray/card "Quit" really exits the process
- **Readable failures** — a rejected key or unreachable network shows the actual reason on the card's refresh line ("Failed · HTTP 401", the server's own message, …); hover the card for likely causes and the log path
- **Multi-monitor safe** — if a monitor is unplugged and the card ends up off-screen, it is pulled back onto a visible display automatically

## How It Works

Reads quota from the GLM Coding Plan monitor endpoint (`GET /api/monitor/usage/quota/limit`) using your Coding Plan API key — the same call the official `glm-plan-usage` plugin makes. Two nested rings: outer = weekly, inner = 5-hour window. Clickable refresh button; auto-refresh every few minutes.

Settings & API key live under `%APPDATA%\BrainFuel\` (Windows) / `~/.config/BrainFuel/` (Linux) / `~/Library/Application Support/BrainFuel/` (macOS).

## Troubleshooting

**Filled in the key but no quota shows.** Look at the card's refresh line — it states the actual failure ("Failed · HTTP 401", "Failed · token expired or incorrect", timeout, …); hovering the card adds likely causes. The two usual suspects:

- **Key/platform mismatch** — a Zhipu (bigmodel.cn) key selected as "Z.ai international" (or vice versa) is rejected by the gateway. Both gateways report bad keys as HTTP 200 with an error body, so versions before 0.2.1 looked "successful but empty"; 0.2.1 onwards this surfaces as an explicit failure.
- **Plan without Coding-Plan usage** — if the account has no such quota, the endpoint returns no token limits; the card says so instead of showing `--`.

Every failure is appended to `brainfuel.log` next to `settings.json` (path shown in the card tooltip), and the raw API response is kept in `quota-debug.json` in the same folder.

## Requirements

| Dependency | Purpose / Version |
|------------|-------------------|
| [.NET 10 SDK](https://dotnet.microsoft.com/) | Runtime / build target |
| [Avalonia 12](https://avaloniaui.net/) | Cross-platform UI framework |
| Windows / macOS / Linux | Supported desktop platforms |

## Quick Start

### 1. Clone

```bash
git clone https://github.com/turinglambdaai/brainfuel.git
cd brainfuel
```

### 2. Run

```bash
dotnet run
```

On first launch, paste your GLM Coding Plan key in the settings dialog.

> If `dotnet build`/`restore` can't reach nuget.org (restricted networks), restore from the local package cache instead:
> ```bash
> dotnet restore --ignore-failed-sources
> ```

## Distribute

**Option A — download from [Releases](https://github.com/turinglambdaai/brainfuel/releases):**

- **Windows installer** — `BrainFuel-Setup-<version>.exe`: a per-user setup (no admin/UAC) that installs into `%LOCALAPPDATA%\Programs\BrainFuel` with Start-menu / optional desktop shortcuts, an optional start-on-login task, and a clean uninstall entry. User data (`%APPDATA%\BrainFuel`) is kept on uninstall.
- **Portable exes** — self-contained single-file zips for `win-x64`, `osx-arm64`, and `linux-x64`; no .NET install needed on the target.

(A manual run from the [Actions tab](https://github.com/turinglambdaai/brainfuel/actions) also produces downloadable artifacts.)

**Option B — build locally:**

```powershell
./publish.ps1                          # portable single-file exe (win-x64 default)
./installer/build-installer.ps1        # Windows: publish + compile the setup exe
```

`build-installer.ps1` reads the version from `BrainFuel.csproj`, publishes `win-x64`, and compiles `installer/BrainFuel.iss`. It finds Inno Setup on PATH / in Program Files, or provisions a private copy under `.tools\` automatically (no admin needed). Output: `dist/BrainFuel-Setup-<version>.exe`.

## Project Structure

```
brainfuel/
├── App.axaml(.cs)            # Application definition / DI container
├── Program.cs                # Entry point
├── MainWindow.axaml(.cs)     # Main widget window (quota rings)
├── NotificationWindow.axaml(.cs)  # Desktop notification window
├── SettingsWindow.axaml(.cs) # Settings + API key dialog
├── Controls/
│   └── UsageRing.cs          # Reusable quota-ring control
├── Services/
│   ├── GlmUsageClient.cs     # GLM Coding Plan quota API client
│   ├── AppLog.cs             # Rolling error log next to the settings file
│   ├── SettingsService.cs    # Settings / API key persistence
│   ├── AutoStartService.cs   # Start-on-login (Win/macOS/Linux)
│   ├── SingleInstanceActivation.cs  # Single-instance guard
│   ├── Strings.cs            # Localized strings
│   └── UsageModels.cs        # Quota data models
├── ViewModels/
│   └── MainViewModel.cs      # MVVM view model
├── installer/                # Inno Setup script + Windows build script
├── publish.ps1               # Local self-contained build script
└── BrainFuel.csproj          # Project file
```

## License

Licensed under the [MIT License](LICENSE).
