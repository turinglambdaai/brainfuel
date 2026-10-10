# BrainFuel

A small desktop widget for monitoring **GLM Coding Plan** quota — the 5-hour rolling window and weekly allowance — without interrupting your work. Also reads OpenAI Codex and Claude (Pro/Max) usage from your local CLI login.

[![CI](https://github.com/turinglambdaai/brainfuel/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/turinglambdaai/brainfuel/actions/workflows/ci.yml) ![Racket](https://img.shields.io/badge/Racket-9.3-blue) ![Swift](https://img.shields.io/badge/macOS-SwiftUI-orange) [![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

**English** · [中文](README.zh-CN.md)

Built on [Rivet](https://github.com/turinglambdaai/rivet): one Racket domain core (426 tests) driving first-party native hosts over typed RPC. The Racket backend owns all business logic — provider clients, burn rate, history, settings, credentials; hosts only render and forward interaction.

## Host status

| Platform | Stack | Status |
|---|---|---|
| macOS | SwiftUI | Working — floating card, tray, notifications, settings |
| Linux | GTK4 | Working — card, details, settings (no tray: rivet #118) |
| Windows | C++/WinRT | Scaffold |

## Product model

- **Quota data** lives on the main card. The visible action is explicitly **Refresh quota**.
- **Three information layers** — L0: the always-visible card (glance, zero interaction). L1: click the card for the detail panel (burn rate, time-to-empty, reset times, plan level). L2: the ⋯ menu and Settings for configuration. No main window, no taskbar noise.
- **Lifecycle in the tray** — closing the card is just "hide": polling and notifications continue, and only the explicit Quit ends the process.
- **Desktop behavior is user-controlled** — Keep on top is optional; new installs do not steal focus.

## Features

- GLM Coding Plan via API key; OpenAI Codex and Claude via your local CLI login (`~/.codex`, `~/.claude` — read fresh on every refresh, no tokens stored)
- Nested weekly / 5-hour rings, urgency colors at 75%/90%, mini mode
- Burn-rate intelligence: per-window consumption speed, projected time-to-empty
- Usage history graphs (last 24 h / 7 days) with previous-period overlay
- Multiple accounts, each polled on its own schedule; instant switching
- API keys in the OS vault: Windows DPAPI, macOS Keychain, Linux Secret Service; legacy plaintext keys migrate automatically
- Bilingual UI (中文/English); light / dark / system theme
- Data formats are drop-in compatible with v0.9.0: same `settings.json`, same usage-history files, same data directories

## Install

Grab your build from
[Releases](https://github.com/turinglambdaai/brainfuel/releases/latest):

| Platform | Portable zip | Installer |
|---|---|---|
| macOS Apple silicon | `brainfuel-<version>-macos-arm64.zip` | `brainfuel-<version>-macos-arm64.dmg` |
| macOS Intel | `brainfuel-<version>-macos-x64.zip` | `brainfuel-<version>-macos-x64.dmg` |
| Windows x64 | `brainfuel-<version>-windows-x64.zip` | `brainfuel-<version>-windows-x64.msi` |
| Linux x64 | `brainfuel-<version>-linux-x64.tar.gz` | `.deb` / `.rpm` / `.AppImage` |
| Linux ARM64 | `brainfuel-<version>-linux-arm64.tar.gz` | `.deb` / `.rpm` / `.AppImage` |

Every release carries a `SHA256SUMS` manifest over all assets.

macOS builds are ad-hoc signed; if Gatekeeper complains on first launch,
run `xattr -cr /Applications/BrainFuel.app`.

Linux needs a GTK 4 desktop (unpack the tarball and run `RivetHost`;
GTK 4 and its system libraries are the only runtime dependencies —
everything else is bundled).

## Build from source

Requires Racket CS 9.x with [Rivet](https://github.com/turinglambdaai/rivet) linked:

```bash
cd ../rivet && raco pkg install --auto --no-docs --name rivet --link file://$PWD
cd ../brainfuel
raco rivet doctor --json   # toolchain check
raco rivet build           # backend bundle + host build
raco test racket/          # domain core tests
```

## Project structure

```text
brainfuel/
├── rivet.rktd          # Rivet app manifest
├── app/backend.rkt     # Rivet backend entry (wires the domain layer)
├── racket/             # Racket domain core + tests
├── shared/i18n/        # zh.json / en.json single source
├── macos-host/         # SwiftUI host (SwiftPM)
├── windows/            # WinUI3 host (scaffold)
├── linux/              # GTK4 host (scaffold)
└── docs/               # website + signing notes
```

## License

Licensed under the [MIT License](LICENSE).
