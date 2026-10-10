# Changelog

All notable changes to BrainFuel are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - 2026-10-10

### Added

- Family-style in-app updater (the taskly port — backend verifies and
  downloads, hosts install): backend `check-updates` / `start-download` /
  `update-state` RPCs over `rivet/distribution` (Ed25519-signed channel
  manifest + sha256-verified artifacts, sticky per-install rollout bucket in
  a two-key `updater-state.json` whitelist) plus `get-setting` /
  `set-setting` for the shared 4-hour silent-check throttle. Release
  pipeline signs `update-manifest.json` from the four portable artifacts
  (`scripts/make-update-manifest.sh`, key: `scripts/update-keys.sh`,
  secret `UPDATE_ED25519_PRIVATE_KEY_B64`; missing secret warns and ships
  without a manifest). macOS host: UpdateService (CryptoKit wrapper verify,
  zip → ditto → in-place swap with `.old` fallback, relaunch), tray and
  Settings ▸ Software entries, update panel, silent launch check. Windows
  host: silent throttled check, card-menu and software-tab entries, progress
  polling, `update-install.cmd` handoff swap with failure marker on next
  launch, MSI-installed copies guided to the releases page. Linux host:
  silent throttled check, card-menu and software-tab entries, progress
  dialog, downloaded dialog with manual-install hint and open-folder.
  Contract and platform table: `shared/spec/UPDATE.md`.

### Changed

- Release preflight (`scripts/check-release-version.sh`) now also checks
  the updater's embedded version against VERSION (VERSION == rivet.rktd ==
  updater == tag).

## [1.1.0] - 2026-10-09

### Changed

- Release engineering aligned with the taskly v1.3.0 packaging benchmark:
  a `VERSION` file with a release preflight (`scripts/check-release-version.sh`,
  run in CI and on every tag) keeps VERSION, `rivet.rktd`, and the git tag
  aligned. macOS now ships both Apple silicon and Intel builds, each as DMG
  and portable zip; Windows adds a portable zip next to the MSI. All assets
  follow the unified lowercase `brainfuel-<version>-<os>-<arch>.<ext>`
  naming, and every release publishes one `SHA256SUMS` covering all assets.

### Removed

- The signed update manifests (`update-stable*.json`) shipped since 1.0.0.
  The app has no update client to consume them — no update RPC, no host
  update UI — so publishing them promised a capability that does not
  exist. Manifests return together with a real updater (backend port of
  taskly's `racket/taskly/updater.rkt` pattern plus host UIs on all three
  platforms).

## [1.0.1] - 2026-10-09

### Fixed

- The update check follows HTTP redirects (rivet#153): GitHub release
  assets answer with a 302 to their CDN, and the previous fetch verified
  an empty redirect body — every in-app update check failed at signature
  verification. No app changes; rebuilt on the fixed rivet.


## [1.0.0] - 2026-10-02

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

[Unreleased]: https://github.com/turinglambdaai/brainfuel/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/turinglambdaai/brainfuel/compare/v1.0.1...v1.1.0
[1.0.1]: https://github.com/turinglambdaai/brainfuel/compare/v1.0.0...v1.0.1
[0.6.7]: https://github.com/turinglambdaai/brainfuel/compare/v0.6.6...v0.6.7
[0.6.6]: https://github.com/turinglambdaai/brainfuel/compare/v0.6.5...v0.6.6
[0.6.5]: https://github.com/turinglambdaai/brainfuel/compare/v0.6.4...v0.6.5
[0.6.4]: https://github.com/turinglambdaai/brainfuel/compare/v0.6.3...v0.6.4
[0.6.3]: https://github.com/turinglambdaai/brainfuel/compare/v0.6.2...v0.6.3
[0.6.2]: https://github.com/turinglambdaai/brainfuel/compare/v0.6.1...v0.6.2
[0.6.1]: https://github.com/turinglambdaai/brainfuel/compare/v0.6.0...v0.6.1
[0.6.0]: https://github.com/turinglambdaai/brainfuel/compare/v0.5.10...v0.6.0
