# BrainFuel

A small, always-on-top desktop widget for monitoring **GLM Coding Plan** quota — the 5-hour rolling window and weekly allowance — without interrupting your work. Built with **Avalonia 12 / .NET 10** for Windows, macOS, and Linux.

![C#](https://img.shields.io/badge/C%23-512BD4?logo=csharp&logoColor=white) [![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

**English** · [中文](README.zh-CN.md)

## Product model

BrainFuel deliberately separates two concepts that should never be confused:

- **Quota data** lives on the main card. The visible action is explicitly **Refresh quota**.
- **Application lifecycle** lives in the top-right **⋯ app menu** and Settings: Settings, software updates, About, hide, and quit.

This keeps the widget focused on one job: glance at quota and get back to work.

## Features

- **Focused quota card** — nested weekly / 5-hour rings, clear percentages, reset timing, and data freshness
- **Explicit quota refresh** — manual **Refresh quota** plus configurable automatic refresh
- **Structured Settings** — General / Account / Notifications / Software tabs instead of one long form
- **Guided first run** — first launch opens directly to Account with setup guidance and API-key validation
- **Windows automatic software updates** — installed builds check GitHub Releases in the background and prefer small Velopack delta packages
- **Visible version information** — Settings, About, widget app menu, and tray all expose the running version
- **Verifiable releases** — official builds include `SHA256SUMS` and GitHub Artifact Attestations
- **Light / Dark / System theme** — with adjustable card opacity
- **Bilingual UI** — Chinese / English
- **Start on login** — Windows registry / macOS LaunchAgent / Linux autostart
- **Quota notifications** — configurable exhaustion threshold
- **Movable, multi-monitor-safe card** — position is remembered and recovered after display topology changes
- **System tray** — hide the widget without stopping quota polling or notifications

## How it works

BrainFuel reads quota data from the GLM Coding Plan monitor endpoint (`GET /api/monitor/usage/quota/limit`) using your Coding Plan API key. Settings live under `%APPDATA%\BrainFuel\` on Windows, `~/.config/BrainFuel/` on Linux, or `~/Library/Application Support/BrainFuel/` on macOS.

Release builds do not continuously persist raw quota responses to disk. Raw response logging is limited to Debug builds for troubleshooting.

## Software updates

The **Windows installed build** uses Velopack. It checks for software updates about 15 seconds after startup and then every six hours. When an update exists it is downloaded in the background; delta packages are used when possible and the full package is the fallback.

A manual software update is available from the **⋯ app menu**, tray menu, and **Settings → Software**. It is intentionally not shown beside quota refresh controls.

Portable builds do not self-update. Download the latest ZIP from Releases and replace the previous files manually.

## Windows installation

### Recommended: one-click Setup

For normal users, download:

`BrainFuel-win-Setup.exe`

The Setup installer is fast and intentionally avoids a long wizard. Starting with v0.4.0 it uses BrainFuel branding and an installation splash while retaining Velopack's simple one-click flow.

### Optional: MSI

v0.4.0 also publishes:

`BrainFuel-win-Setup.msi`

The MSI is intended for users or administrators who prefer a conventional Windows Installer flow. It includes welcome, MIT license, and completion pages and installs per-user by default. After installation, application updates work the same way as the Setup-installed build.

### SmartScreen and verification

BrainFuel is free and open source and currently does **not** purchase a commercial Authenticode certificate. Windows may therefore show SmartScreen or **Unknown publisher** warnings.

Every official Release includes `SHA256SUMS`:

```powershell
Get-FileHash .\BrainFuel-win-Setup.exe -Algorithm SHA256
Get-Content .\SHA256SUMS
```

With GitHub CLI you can also verify build provenance:

```powershell
gh attestation verify .\BrainFuel-win-Setup.exe --repo turinglambdaai/brainfuel
```

The same mechanism applies to the MSI, portable ZIPs, and Velopack packages. Artifact Attestations prove provenance; they are not Authenticode signatures and do not suppress SmartScreen. See [`SECURITY.md`](SECURITY.md).

## Distribution

Only download official binaries from this repository's [Releases](https://github.com/turinglambdaai/brainfuel/releases) page.

| Asset | Intended use |
|---|---|
| `BrainFuel-win-Setup.exe` | **Recommended Windows install** |
| `BrainFuel-win-Setup.msi` | Conventional / managed Windows installation |
| `BrainFuel-windows-x64.zip` | Windows portable |
| `BrainFuel-macos-arm64.zip` | macOS portable |
| `BrainFuel-linux-x64.zip` | Linux portable |

Portable builds are self-contained and do not require .NET on the target machine. macOS and Linux are currently distributed as portable builds.

## Quick start from source

```bash
git clone https://github.com/turinglambdaai/brainfuel.git
cd brainfuel
dotnet run
```

On first launch, paste your GLM Coding Plan key into **Settings → Account**.

## Release engineering

Local Windows packaging:

```powershell
./installer/build-velopack.ps1
./installer/build-velopack.ps1 -DownloadPrevious
```

The packaging script creates the branded one-click Setup, optional MSI, full package, delta package when a previous release is available, and the Velopack update feed.

Official GitHub Actions releases additionally:

- build Windows / macOS / Linux portable artifacts;
- require the Windows Setup, MSI, full package, feed, and published delta package;
- create GitHub Artifact Attestations for release assets;
- generate and verify `SHA256SUMS` before publishing the Release.

## Project structure

```text
brainfuel/
├── App.axaml(.cs)                 # application lifecycle / tray / background services
├── MainWindow.axaml(.cs)          # focused quota widget + app menu
├── SettingsWindow.axaml(.cs)      # General / Account / Notifications / Software
├── AboutWindow.axaml(.cs)         # version / build / project links
├── NotificationWindow.axaml(.cs)  # desktop notification window
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
│   ├── build-velopack.ps1         # branded Setup + MSI + update feed
│   ├── welcome.md
│   └── conclusion.md
├── .github/workflows/
│   ├── ci.yml
│   └── release.yml
├── SECURITY.md
└── BrainFuel.csproj
```

## License

Licensed under the [MIT License](LICENSE).
