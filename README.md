# brainfuel

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

**Option A — download a prebuilt exe** from [Releases](https://github.com/turinglambdaai/brainfuel/releases). The `release` workflow builds self-contained single-file exes for `win-x64`, `osx-arm64`, and `linux-x64` — no .NET install needed on the target. (A manual run from the [Actions tab](https://github.com/turinglambdaai/brainfuel/actions) also produces downloadable artifacts.)

**Option B — build locally:**

```powershell
./publish.ps1                 # win-x64 (default)
./publish.ps1 osx-arm64       # macOS Apple Silicon
./publish.ps1 linux-x64       # Linux
```

Output goes to `publish/<rid>/`.

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
├── publish.ps1               # Local self-contained build script
└── BrainFuel.csproj          # Project file
```

## License

Licensed under the [MIT License](LICENSE).
