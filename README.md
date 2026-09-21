# BrainFuel

A tiny always-on-top desktop widget that monitors your **GLM Coding Plan** quota — the 5-hour rolling window and weekly allowance — so you do not get blindsided by a rate limit mid-session. Built with **Avalonia 12** / .NET 10 for Windows, macOS, and Linux.

![C#](https://img.shields.io/badge/C%23-512BD4?logo=csharp&logoColor=white) [![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

**English** · [中文](README.zh-CN.md)

## Features

- **Guided first run** — settings opens automatically on first launch with a quick-start note, console link, and API-key validation
- **Nested quota rings** — outer ring = weekly allowance, inner ring = 5-hour rolling window
- **Auto-refresh** — manual refresh plus configurable periodic refresh
- **Windows automatic updates** — installed builds check GitHub Releases in the background, prefer Velopack delta packages, and fall back to a full package when required; users can also check and install immediately from the card menu or Settings
- **Light / Dark / System theme** — with adjustable card opacity
- **Bilingual UI** — Chinese / English
- **Start-on-login** — Windows registry / macOS LaunchAgent / Linux autostart
- **Quota notifications** — configurable exhaustion threshold
- **Movable card** — position is remembered
- **System tray** — hide without stopping polling or notifications; relaunching the executable activates the running instance
- **Multi-monitor safe** — an off-screen widget is moved back onto a visible display after monitor changes

## How It Works

BrainFuel reads quota data from the GLM Coding Plan monitor endpoint (`GET /api/monitor/usage/quota/limit`) using your Coding Plan API key. Settings and the key live under `%APPDATA%\BrainFuel\` (Windows), `~/.config/BrainFuel/` (Linux), or `~/Library/Application Support/BrainFuel/` (macOS).

Release builds no longer continuously persist the raw quota response to disk. Raw response logging is limited to Debug builds for troubleshooting.

## Automatic Updates

The installed Windows build uses Velopack. It checks for updates about 15 seconds after startup and then every six hours. When an update exists, it is downloaded in the background. Velopack uses delta packages when possible and automatically falls back to the full package when a delta is unavailable or unsuitable. Background-staged updates are applied on the next normal launch; a manual **Check for updates** action downloads, installs, and restarts immediately.

Portable builds intentionally do not self-update. Settings identifies the current build as **Windows installed build** or **Portable build**. Portable users get a direct link to the latest GitHub Release and update by replacing the old executable with the new ZIP contents.

### Migrating from v0.2.x and earlier

Older BrainFuel versions use the Inno Setup installer and do not contain the Velopack update engine, so they **cannot automatically upgrade themselves to the first Velopack-enabled release**. This is a one-time migration:

1. Quit the old BrainFuel instance.
2. Uninstall the old application. User data under `%APPDATA%\BrainFuel` is preserved.
3. Download and run the new `BrainFuel-win-Setup.exe` from Releases.

Subsequent installed Windows releases update in-app. Portable ZIP builds remain manually replaceable, and the current macOS/Linux releases are still portable builds rather than auto-updating installations.

## Requirements

| Dependency | Purpose / Version |
|------------|-------------------|
| [.NET 10 SDK](https://dotnet.microsoft.com/) | Runtime / build target |
| [Avalonia 12](https://avaloniaui.net/) | Cross-platform UI framework |
| Windows / macOS / Linux | Supported desktop platforms |

## Quick Start

```bash
git clone https://github.com/turinglambdaai/brainfuel.git
cd brainfuel
dotnet run
```

On first launch, paste your GLM Coding Plan key into Settings.

> If `dotnet build`/`restore` cannot reach nuget.org on a restricted network, try `dotnet restore --ignore-failed-sources` when the required packages already exist in your local cache.

## Distribution

Download from [Releases](https://github.com/turinglambdaai/brainfuel/releases).

### Recommended for Windows

**Use `BrainFuel-win-Setup.exe` unless you specifically need a portable copy.** The installed build is the normal end-user distribution: it supports automatic background checks, tiny delta updates when available, one-click manual update/install/restart, normal uninstall behavior, and clear version/build information in Settings.

### Portable / advanced use

Portable ZIPs are kept for scenarios where installation is undesirable or unavailable:

- `BrainFuel-windows-x64.zip`
- `BrainFuel-macos-arm64.zip`
- `BrainFuel-linux-x64.zip`

They are self-contained and do not require .NET on the target machine. Portable builds **do not modify their own directory and do not auto-update**; download the latest ZIP and replace the previous files manually. This makes them appropriate for temporary testing, locked-down machines, removable drives, and troubleshooting.

macOS and Linux are currently distributed only as portable builds.

Local builds:

```powershell
./publish.ps1
./installer/build-velopack.ps1
./installer/build-velopack.ps1 -DownloadPrevious
```

The repository pins the `vpk` CLI version in `.config/dotnet-tools.json` so the packaging tool matches the Velopack SDK. On tag releases, GitHub Actions downloads the previous Velopack release when available, generates the new full/delta feed, gathers all platform artifacts, and publishes the GitHub Release in one final job.

## Project Structure

```text
brainfuel/
├── App.axaml(.cs)                 # application lifecycle / background services
├── Program.cs                     # entry point / Velopack startup hook
├── MainWindow.axaml(.cs)          # quota widget
├── NotificationWindow.axaml(.cs)  # desktop notification window
├── SettingsWindow.axaml(.cs)      # settings + API key dialog
├── Controls/
│   └── UsageRing.cs
├── Services/
│   ├── GlmUsageClient.cs
│   ├── UpdateService.cs           # background update checks/downloads
│   ├── SettingsService.cs
│   ├── AutoStartService.cs
│   ├── SingleInstanceActivation.cs
│   ├── Strings.cs
│   └── UsageModels.cs
├── installer/
│   ├── build-velopack.ps1         # Windows installer + delta-update feed
│   └── build-installer.ps1        # legacy Inno Setup packaging reference
├── .github/workflows/
│   ├── ci.yml
│   └── release.yml
└── BrainFuel.csproj
```

## License

Licensed under the [MIT License](LICENSE).
