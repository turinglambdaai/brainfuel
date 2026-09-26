# BrainFuel

A small desktop widget for monitoring **GLM Coding Plan** quota — the 5-hour rolling window and weekly allowance — without interrupting your work. Built with **Avalonia 12 / .NET 10** for Windows, macOS, and Linux.

![C#](https://img.shields.io/badge/C%23-512BD4?logo=csharp&logoColor=white) [![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

**English** · [中文](README.zh-CN.md)

## Product model

BrainFuel deliberately separates concepts that should not be confused:

- **Quota data** lives on the main card. The visible action is explicitly **Refresh quota**.
- **Application lifecycle** lives in the top-right **⋯ app menu** and Settings: Settings, software updates, display movement, About, hide, and quit. Closing the card (X button / Alt+F4 / system menu) is also just "hide": a transient toast points at the tray, and only the explicit **Quit** ends the process — monitoring never dies with the window.
- **Desktop behavior** is user-controlled. New installs do not force the card above every application; **Keep on top** is optional.

The goal is simple: glance at quota and get back to work.

## Features

- **Focused quota card** — nested weekly / 5-hour rings, clear percentages, reset timing, and data freshness
- **Explicit quota refresh** — manual **Refresh quota** plus configurable automatic refresh
- **Secure API-key storage** — Windows DPAPI, macOS Keychain, and Linux Secret Service when available; legacy plaintext keys are migrated automatically
- **Structured Settings** — General / Account / Notifications / Software tabs instead of one long form
- **Non-intrusive desktop behavior** — new installs start non-topmost and do not steal focus; Keep on top is an explicit preference
- **Monitor-aware placement** — remembers the physical display plus relative position, handles DPI/resolution/taskbar changes, and recovers when an external display disappears
- **Display controls** — app menu can move the card to the primary or next display
- **Guided first run** — first launch opens directly to Account with setup guidance and API-key validation
- **Windows automatic software updates** — installed builds check GitHub Releases in the background and prefer small Velopack delta packages
- **Visible version information** — Settings, About, widget app menu, and tray expose the running version
- **Verifiable releases** — official builds include `SHA256SUMS` and GitHub Artifact Attestations
- **Light / Dark / System theme** — with adjustable card opacity
- **Bilingual UI** — Chinese / English
- **Start on login** — Windows registry / macOS LaunchAgent / Linux autostart
- **Quota notifications** — configurable exhaustion threshold
- **System tray** — hide the widget (also via close / Alt+F4) without stopping quota polling or notifications
- **Readable quota failures** — the card's freshness line names the failure category (bad key/platform mismatch, network, proxy, timeout, no plan…); hovering the card shows the full guidance and the log path

## Desktop and multi-monitor behavior

BrainFuel stores the card's position relative to the **working area** of the display rather than depending only on absolute desktop pixels.

The v0.5 behavior is intentionally conservative:

- a new install starts near the **upper-right of the primary display**, with an edge margin;
- dragging the card remembers the display and relative position;
- if that display is unplugged, the card moves to the **primary display at the same relative position**;
- reconnecting the old display does **not** unexpectedly pull the card away from the screen where you are currently working;
- display rearrangement, resolution/orientation changes, DPI scaling changes, and taskbar/dock changes keep the complete card inside a usable working area;
- **⋯ → Move to next display / Move to primary display** provides an explicit recovery path;
- **Keep on top** is off by default for new installs so normal work windows can cover BrainFuel. Users upgrading from versions that were always-on-top keep their previous behavior until they change the preference.

BrainFuel also starts with `ShowActivated=false`, so an automatic launch does not intentionally steal keyboard focus. Restoring it from the tray or launching it again is an explicit user action and brings it forward.

## API-key storage

BrainFuel reads quota data from the GLM Coding Plan monitor endpoint (`GET /api/monitor/usage/quota/limit`) using your Coding Plan API key.

From **v0.5.0**, the key is normally kept outside `settings.json`:

| Platform | Preferred credential storage |
|---|---|
| Windows | DPAPI, protected for the current Windows user |
| macOS | Login Keychain |
| Linux | freedesktop Secret Service via `secret-tool` |

On the first v0.5 launch, an older plaintext key is migrated automatically when the platform credential store is available. If an already-protected credential store is temporarily unavailable, BrainFuel does not downgrade the key to plaintext and retries access during later quota refreshes.

If a **new or changed** key must be saved while no supported system secret store is available, BrainFuel falls back to the local settings file rather than losing the credential. Settings clearly shows this state, and Unix settings files are restricted to the current user where supported. See [`SECURITY.md`](SECURITY.md) for the detailed threat/edge-case model.

Non-secret preferences live under `%APPDATA%\BrainFuel\` on Windows, `~/.config/BrainFuel/` on Linux, or `~/Library/Application Support/BrainFuel/` on macOS.

Release builds do not continuously persist raw quota responses to disk. Raw response logging is limited to Debug builds for troubleshooting. Quota *failures* (category + server message, never the key or raw payload) are appended to a rolling `brainfuel.log` beside `settings.json` so "the key is set but nothing refreshes" can be diagnosed after the fact; the path is shown in the card's failure tooltip.

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

BrainFuel also publishes:

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

```bash
dotnet test BrainFuel.Tests/BrainFuel.Tests.csproj   # failure-classification regression tests (also run in CI)
```

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
├── MainWindow.axaml(.cs)          # focused quota widget + desktop/app menu behavior
├── SettingsWindow.axaml(.cs)      # General / Account / Notifications / Software
├── AboutWindow.axaml(.cs)         # version / build / project links
├── NotificationWindow.axaml(.cs)  # desktop notification window
├── Controls/UsageRing.cs
├── Services/
│   ├── CredentialStore.cs         # DPAPI / Keychain / Linux Secret Service
│   ├── WindowPlacementService.cs  # monitor-aware relative placement
│   ├── GlmUsageClient.cs
│   ├── AppLog.cs                 # rolling errors-only log (no keys/payloads)
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
├── BrainFuel.Tests/           # xUnit tests (quota failure classification, retry semantics)
├── SECURITY.md
└── BrainFuel.csproj
```

## License

Licensed under the [MIT License](LICENSE).
