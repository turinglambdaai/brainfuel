# Security Policy

## Official releases

The only official BrainFuel binary distribution is the GitHub Releases page for this repository:

- https://github.com/turinglambdaai/brainfuel/releases

Do not trust installers or ZIP files redistributed from unrelated download sites.

BrainFuel is free and open source. The Windows installer is intentionally not Authenticode-signed with a paid commercial certificate, so Windows SmartScreen may display an **Unknown publisher** or reputation warning even for an authentic release.

Starting with **v0.3.5**, official release artifacts are protected by two free verification mechanisms:

1. **SHA-256 manifest** — every Release includes `SHA256SUMS`.
2. **GitHub Artifact Attestations** — release binaries are attested from the GitHub Actions jobs that build them, using GitHub's Sigstore-backed attestation service.

### Verify a SHA-256 checksum

On Windows PowerShell:

```powershell
Get-FileHash .\BrainFuel-win-Setup.exe -Algorithm SHA256
Get-Content .\SHA256SUMS
```

The hash printed for `BrainFuel-win-Setup.exe` must exactly match the corresponding entry in `SHA256SUMS`.

On Linux/macOS:

```bash
sha256sum --check SHA256SUMS
```

### Verify build provenance

With the GitHub CLI installed:

```bash
gh attestation verify BrainFuel-win-Setup.exe --repo turinglambdaai/brainfuel
```

The same command can be used for the portable ZIP files and Velopack packages.

A successful attestation verification establishes that the exact artifact digest was attested by a GitHub Actions workflow associated with this repository. It is not an Authenticode signature and therefore does not suppress Windows SmartScreen warnings.

## API-key storage

Starting with **v0.5.0**, BrainFuel no longer treats `settings.json` as the normal storage location for the GLM Coding Plan API key.

BrainFuel prefers the operating system's per-user credential protection:

| Platform | Preferred storage |
| --- | --- |
| Windows | DPAPI (`CurrentUser`) protected credential blob |
| macOS | Login Keychain through `Security.framework` |
| Linux | freedesktop Secret Service through `secret-tool` (for example GNOME Keyring / compatible providers) |

`settings.json` contains a non-secret marker indicating whether a credential is configured, but the plaintext key is removed when protected storage succeeds.

### Migration from older versions

A key stored by v0.4.x or earlier may still exist in plaintext in `settings.json` before the first v0.5.0 launch. On load, BrainFuel attempts to move that key into the platform credential store. When migration succeeds, it rewrites `settings.json` without the plaintext key.

### Secure-store failures

The credential store can occasionally be unavailable — for example, a Linux Secret Service session may not be running or a macOS Keychain may be temporarily inaccessible. BrainFuel handles these cases conservatively:

- an already-protected key is **not downgraded to plaintext** merely because the credential store is temporarily unavailable;
- unrelated preference saves do not rewrite or delete an unchanged protected key;
- the application keeps a configured-credential marker and retries protected-key access during later quota refreshes;
- clearing a credential writes an explicit tombstone so a stale secure-store entry cannot be silently resurrected;
- Settings shows the current storage state instead of claiming protection that is not available.

If a user saves a **new or changed** key while no supported OS credential store is available, BrainFuel preserves usability by falling back to `settings.json` rather than silently losing the key. On Linux/macOS the settings file is restricted to the current user (`0600`) where the filesystem supports Unix permissions. This fallback is explicitly shown in Settings and should be considered less desirable than OS-protected storage.

Settings writes are performed through a temporary file and atomic replacement to reduce the chance of a partially-written preferences file after a crash or power loss.

## Sensitive data and diagnostics

Never include your GLM Coding Plan API key, `settings.json`, `credentials.dat`, screenshots containing credentials, or other secrets in an issue, discussion, log excerpt, or crash report.

Release builds do not continuously persist raw quota responses. Debug-only diagnostics should still be reviewed before sharing because service responses can contain account-specific information.

## Reporting a vulnerability

For security issues that can be discussed publicly, open a GitHub issue with a minimal reproduction and affected version.

If a report contains exploit details or other information that should not be public, use GitHub's **Report a vulnerability** / private vulnerability reporting flow if it is available for the repository. Do not publish secrets or working exploit details in a public issue.

When reporting, include:

- affected BrainFuel version;
- operating system and architecture;
- reproduction steps;
- expected and observed behavior;
- impact assessment;
- relevant logs with credentials and personal data removed.
