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

## API keys and sensitive data

Never include your GLM Coding Plan API key, `settings.json`, screenshots containing credentials, or other secrets in an issue, discussion, log excerpt, or crash report.

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
