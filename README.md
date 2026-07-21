# brainfuel

A tiny always-on-top desktop widget that monitors your **GLM Coding Plan** quota — the 5-hour rolling window and the weekly allowance — so you don't get blindsided by a rate limit mid-session.

Built with **Avalonia 12** / .NET 10. Cross-platform (Windows / macOS / Linux).

## Run

```bash
dotnet run
```

On first launch, paste your GLM Coding Plan key in the settings dialog.

## How it works

Reads quota from the GLM Coding Plan monitor endpoint (`GET /api/monitor/usage/quota/limit`) using your Coding Plan API key — the same call the official `glm-plan-usage` plugin makes. Two nested rings: outer = weekly, inner = 5-hour window. Clickable refresh button; auto-refresh every few minutes.

Settings & API key live under `%APPDATA%\BrainFuel\` (Windows) / `~/.config/BrainFuel/` (Linux) / `~/Library/Application Support/BrainFuel/` (macOS).

## Distribute

Self-contained single-file exe (no .NET install needed on the target machine):

```powershell
./publish.ps1
```
