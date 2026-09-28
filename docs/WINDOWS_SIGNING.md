# Windows production code signing

BrainFuel supports production Windows code signing with Azure Artifact Signing, but signing is currently **optional** so public releases do not require a paid signing service during the project's early stage.

When the complete Azure signing configuration is present, the Windows release job signs all of:

- `BrainFuel.exe` inside the Velopack payload — before `vpk pack`, so the installed app and every full/delta update package carry the signed binary;
- `BrainFuel-win-Setup.exe` after the Velopack package is built; and
- `BrainFuel-win-Setup.msi`.

The workflow then runs `Get-AuthenticodeSignature` on each file and requires the status to be `Valid`.

When none of the Azure signing variables are configured, the same workflow publishes unsigned Windows binaries with checksums and attestations. A partially configured signing setup is treated as an error: configure all six variables or none of them.

## Why Azure Artifact Signing

BrainFuel is prepared to use [Azure Artifact Signing](https://learn.microsoft.com/azure/artifact-signing/overview) (formerly Trusted Signing) with a **Public Trust** certificate profile.

This avoids checking a long-lived private signing key into GitHub. GitHub Actions authenticates to Azure with OpenID Connect (OIDC), and the signing key remains in Microsoft's managed HSM-backed service.

A self-signed certificate is deliberately not used for public releases because Windows does not trust it by default and it does not solve the SmartScreen publisher-trust problem.

## SmartScreen expectation

Authenticode signing materially improves the Windows trust experience, but a brand-new publisher identity can still receive an "unrecognized app" SmartScreen warning while reputation is being established. Keep signing every public release with the same publisher identity once production signing is enabled so reputation can accumulate across versions.

Until signing is enabled, Windows may identify the direct-download installer as coming from an unknown publisher. `SHA256SUMS` and the release attestations still provide an integrity check, but they are not a substitute for publisher authentication.

## One-time Azure setup

When production signing becomes worthwhile:

1. In Azure, register/use the **Microsoft.CodeSigning / Artifact Signing** resource provider.
2. Create an Artifact Signing account.
3. Complete **Public** identity validation.
4. Create a **Public Trust** certificate profile. Do not use `Public Trust Test` for production; test profiles are not publicly trusted.
5. Create or reuse a Microsoft Entra application/service principal for GitHub Actions.
6. Add a federated credential for this repository so GitHub Actions can authenticate with OIDC. Scope it to `turinglambdaai/brainfuel` and the release context you intend to use.
7. Assign that service principal the **Artifact Signing Certificate Profile Signer** role on the production certificate profile (or the narrowest supported scope containing it).

Microsoft's setup references:

- https://learn.microsoft.com/azure/artifact-signing/quickstart
- https://learn.microsoft.com/azure/artifact-signing/tutorial-assign-roles
- https://learn.microsoft.com/azure/artifact-signing/how-to-signing-integrations
- https://github.com/Azure/artifact-signing-action

## Optional GitHub repository variables

Configure these together under **Settings → Secrets and variables → Actions → Variables** when signing is enabled:

| Variable | Meaning |
| --- | --- |
| `AZURE_CLIENT_ID` | Microsoft Entra application/client ID used by GitHub OIDC |
| `AZURE_TENANT_ID` | Microsoft Entra tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Azure subscription containing the Artifact Signing resource |
| `AZURE_ARTIFACT_SIGNING_ENDPOINT` | Artifact Signing account endpoint, for example the regional `https://...codesigning.azure.net/` endpoint shown by Azure |
| `AZURE_ARTIFACT_SIGNING_ACCOUNT` | Artifact Signing account name |
| `AZURE_ARTIFACT_SIGNING_PROFILE` | Production **Public Trust** certificate profile name |

These identifiers are not private signing keys. The workflow intentionally uses OIDC and does not require an `AZURE_CLIENT_SECRET`.

## Release behavior

The Windows updater job performs this sequence:

1. Inspect the six signing variables.
2. If all are absent, select unsigned mode. If some but not all are present, fail the job. If all are present, select signed mode.
3. Publish the multi-file win-x64 payload (phase `publish` of `installer/build-velopack.ps1`).
4. In signed mode, authenticate to Azure, sign `BrainFuel.exe`, and require its Authenticode status to be `Valid`.
5. Pack the payload into the branded Setup, MSI, full/delta packages and feed (phase `pack`).
6. In signed mode, sign `BrainFuel-win-Setup.exe` and `BrainFuel-win-Setup.msi` and require both signatures to be `Valid`.
7. Verify the required artifact set, attest every artifact, and upload.
8. Allow the publish job to run only after every artifact arrives and the SHA256SUMS manifest verifies.

This keeps signing ready to turn on later without maintaining a separate release pipeline.

## Verifying a downloaded build locally

On Windows PowerShell:

```powershell
Get-AuthenticodeSignature .\BrainFuel-win-Setup.exe |
  Format-List Status, StatusMessage, SignerCertificate, TimeStamperCertificate
```

For an unsigned release, `Status` will indicate that no valid Authenticode signature is present. Verify the file against `SHA256SUMS` instead (and optionally `gh attestation verify`).

For a signed production build, `Status` should be `Valid` and `SignerCertificate` should identify the verified BrainFuel publisher identity from the Artifact Signing profile.

You can also inspect signed files through **Properties → Digital Signatures**.

## Existing releases

Releases up to and including `v0.6.7` are unsigned. Future releases remain publishable without Azure while retaining the ability to switch to trusted signing simply by configuring all six repository variables.
