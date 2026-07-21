# brainfuel

A tiny always-on-top desktop widget that monitors your **GLM Coding Plan** quota — the 5-hour rolling window and the weekly allowance — so you don't get blindsided by a rate limit mid-session.

Built with **Avalonia 12** / .NET 10. Cross-platform (Windows / macOS / Linux).

## Run

```bash
dotnet run
```

On first launch, paste your GLM Coding Plan key in the settings dialog.

> If `dotnet build`/`restore` can't reach nuget.org (restricted networks), restore from the local package cache instead:
> ```bash
> dotnet restore --ignore-failed-sources
> ```

## How it works

Reads quota from the GLM Coding Plan monitor endpoint (`GET /api/monitor/usage/quota/limit`) using your Coding Plan API key — the same call the official `glm-plan-usage` plugin makes. Two nested rings: outer = weekly, inner = 5-hour window. Clickable refresh button; auto-refresh every few minutes.

Settings & API key live under `%APPDATA%\BrainFuel\` (Windows) / `~/.config/BrainFuel/` (Linux) / `~/Library/Application Support/BrainFuel/` (macOS).

## Distribute

**Option A — download a prebuilt exe** from [Releases](https://github.com/turinglambdaai/brainfuel/releases). The `release` workflow builds self-contained single-file exes for `win-x64`, `osx-arm64`, and `linux-x64` — no .NET install needed on the target. (A manual run from the [Actions tab](https://github.com/turinglambdaai/brainfuel/actions) also produces downloadable artifacts.)

**Option B — build locally:**

```powershell
./publish.ps1                 # win-x64 (default)
./publish.ps1 osx-arm64       # macOS Apple Silicon
./publish.ps1 linux-x64       # Linux
```

Output goes to `publish/<rid>/`.

## Features

- Two nested rings (outer = weekly, inner = 5-hour window), animated.
- Clickable refresh button; auto-refresh every N minutes.
- **Light / Dark / System theme** (Anthropic-style palette) and a **card opacity** slider so it doesn't block your desktop.
- **中文 / English** UI toggle.
- Optional **start-on-login** (Windows registry / macOS LaunchAgent / Linux autostart).
- Optional **desktop notification** when a quota crosses an exhaustion threshold (default 80% used, configurable).
- Right-click the card for refresh / settings / quit. Drag to move (position remembered).
