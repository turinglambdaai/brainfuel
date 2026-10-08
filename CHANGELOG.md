# Changelog

All notable changes to BrainFuel are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Entries start at 0.6.0; for the 0.1–0.5 history, see the
[GitHub Releases](https://github.com/turinglambdaai/brainfuel/releases) page.

## [Unreleased]

## [0.10.0] - 2026-10-02

### Changed

- Complete rebuild on Rivet: one Racket domain core (426 tests) drives
  first-party native hosts over typed RPC — SwiftUI on macOS, GTK4 on Linux,
  WinUI 3 on Windows. The legacy Avalonia/.NET stack is retired; settings,
  usage history, and OS-keyring credentials are drop-in compatible with 0.9.0.

### Added

- Linux host: floating quota card, details popover with history graphs,
  structured settings, mini mode, single instance, notifications, autostart
  (tray pending the upstream rivet tray contract — closing the card quits).
- Windows host: quota card with severity rings, details dialog with burn
  projections, settings, mini mode, keep-on-top, single instance, autostart.
- Signed self-contained update feeds (Ed25519) for Windows and Linux;
  SHA256SUMS and build provenance attestation on every release.

### Fixed

- Floating card placement parked 348 pt off-screen on macOS.
- Usage-history samples recorded within the same millisecond could collapse
  into a machine-dependent order on fast machines.

## [0.6.7] - 2026-09-28

### Added

- Support credit-based plans (Lite and newer) in quota mapping.

## [0.6.6] - 2026-09-28

### Fixed

- Draw the tray menu dots directly instead of relying on the glyph.

## [0.6.5] - 2026-09-28

### Added

- Support newer GLM plans: save-anyway for no-plan accounts, structural failure logs.

## [0.6.4] - 2026-09-28

### Changed

- Settings polish: drop header version badge, uncramp Software tab, unclip footer buttons.

## [0.6.3] - 2026-09-28

### Fixed

- Mini-card menu crash; global exception logging; mini button polish.

## [0.6.2] - 2026-09-28

### Changed

- Center dialog button captions; style the intended accent save button.

## [0.6.1] - 2026-09-27

### Fixed

- Alert toasts now appear on the card's display.

## [0.6.0] - 2026-09-27

### Added

- Multi-account support and previous-period graph overlay.

[Unreleased]: https://github.com/turinglambdaai/brainfuel/compare/v0.6.7...HEAD
[0.6.7]: https://github.com/turinglambdaai/brainfuel/compare/v0.6.6...v0.6.7
[0.6.6]: https://github.com/turinglambdaai/brainfuel/compare/v0.6.5...v0.6.6
[0.6.5]: https://github.com/turinglambdaai/brainfuel/compare/v0.6.4...v0.6.5
[0.6.4]: https://github.com/turinglambdaai/brainfuel/compare/v0.6.3...v0.6.4
[0.6.3]: https://github.com/turinglambdaai/brainfuel/compare/v0.6.2...v0.6.3
[0.6.2]: https://github.com/turinglambdaai/brainfuel/compare/v0.6.1...v0.6.2
[0.6.1]: https://github.com/turinglambdaai/brainfuel/compare/v0.6.0...v0.6.1
[0.6.0]: https://github.com/turinglambdaai/brainfuel/compare/v0.5.10...v0.6.0
