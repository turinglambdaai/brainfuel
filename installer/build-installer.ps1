# Build the BrainFuel Windows installer end to end:
#   1. dotnet publish (self-contained single-file, win-x64)
#   2. compile installer/BrainFuel.iss with Inno Setup (ISCC)
# Output: dist/BrainFuel-Setup-<version>.exe
#
# ISCC lookup order: PATH -> Program Files -> .tools\inno-setup (auto-provisioned
# portable-style copy; Inno's own installer is asInvoker, so no admin needed).
# Works locally and on CI (windows-latest).

param(
    [string]$Rid = "win-x64"
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$InstallerDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root = Split-Path -Parent $InstallerDir
Set-Location $Root

# --- Version comes from the csproj so there is a single source of truth ---
$csproj = Get-Content (Join-Path $Root "BrainFuel.csproj") -Raw
if ($csproj -notmatch '<Version>([^<]+)</Version>') { throw "No <Version> found in BrainFuel.csproj" }
$Version = $Matches[1].Trim()
Write-Host "== BrainFuel $Version ($Rid) =="

# --- 1. Publish (same flags as publish.ps1) ---
dotnet publish -c Release -r $Rid --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    -o "publish/$Rid"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$exe = "publish/$Rid/BrainFuel.exe"
if (-not (Test-Path $exe)) { throw "Expected $exe not found" }

# --- 2. Locate (or provision) the Inno Setup compiler ---
$iscc = $null
foreach ($candidate in @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1),
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\InnoSetup-Private\ISCC.exe",
    (Join-Path $Root ".tools\inno-setup\ISCC.exe")
)) {
    if ($candidate -and (Test-Path $candidate)) { $iscc = $candidate; break }
}

if (-not $iscc) {
    Write-Host "== Inno Setup not found - provisioning a private copy into .tools =="
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $tools = Join-Path $Root ".tools"
    $bootstrapper = Join-Path $tools "innosetup-6.7.3.exe"
    Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe" -OutFile $bootstrapper
    # Inno's own installer is asInvoker: /CURRENTUSER silent install into .tools needs no admin.
    $p = Start-Process -FilePath $bootstrapper -Wait -PassThru -ArgumentList "/CURRENTUSER","/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART","/DIR=$(Join-Path $tools 'inno-setup')"
    if ($p.ExitCode -ne 0) { throw "Inno Setup bootstrap failed (exit $($p.ExitCode))" }
    $iscc = Join-Path $tools "inno-setup\ISCC.exe"
}

Write-Host "== Compiling installer with $iscc =="
# Run from the installer dir so the relative paths in BrainFuel.iss resolve.
Push-Location $InstallerDir
try {
    & $iscc "/DAppVersion=$Version" "BrainFuel.iss"
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
} finally { Pop-Location }

$out = Join-Path $Root "dist\BrainFuel-Setup-$Version.exe"
if (-not (Test-Path $out)) { throw "Expected output $out not found" }
Write-Host ""
Write-Host ("Done -> {0} ({1:N1} MB)" -f $out, ((Get-Item $out).Length / 1MB))
