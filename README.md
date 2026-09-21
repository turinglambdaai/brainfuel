# BrainFuel

A tiny always-on-top desktop widget that monitors your **GLM Coding Plan** quota — the 5-hour rolling window and the weekly allowance — so you don't get blindsided by a rate limit mid-session. Built with **Avalonia 12** / .NET 10. Cross-platform (Windows / macOS / Linux).

![C#](https://img.shields.io/badge/C%23-512BD4?logo=csharp&logoColor=white) [![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

**English** · [中文](README.zh-CN.md)

## Features

- **Nested quota rings** — outer ring = weekly allowance, inner ring = 5-hour rolling window, animated
- **Auto-refresh** — clickable refresh button plus automatic refresh every few minutes
- **Light / Dark / System theme** — Anthropic-style palette, with a card opacity slider so it doesn't block your desktop
- **Bilingual UI** — Chinese / English toggle
- **Start-on-login** — optional autostart (Windows registry / macOS LaunchAgent / Linux autostart)
- **Quota notifications** — optional desktop notification when a quota crosses an exhaustion threshold (default 80% used, configurable)
- **Movable card** — right-click for refresh / settings / quit; drag to move (position remembered)
- **System tray** — hide to tray keeps polling and notifications running; bring the widget back from the tray icon (or by launching the exe again). Only the tray/card "Quit" really exits
- **Multi-monitor safe** — if a monitor is unplugged and the card ends up off-screen, it is pulled back onto a visible display automatically

## How It Works

Reads quota from the GLM Coding Plan monitor endpoint (`GET /api/monitor/usage/quota/limit`) using your Coding Plan API key — the same call the official `glm-plan-usage` plugin makes. Two nested rings: outer = weekly, inner = 5-hour window. Clickable refresh button; auto-refresh every few minutes.

Settings & API key live under `%APPDATA%\BrainFuel\` (Windows) / `~/.config/BrainFuel/` (Linux) / `~/Library/Application Support/BrainFuel/` (macOS).

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
