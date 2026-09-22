# Build BrainFuel Windows release assets with Velopack.
#
# Product policy:
# - BrainFuel-win-Setup.exe remains the recommended fast one-click installer.
# - Setup shows a lightweight BrainFuel-branded splash instead of the bare default UI.
# - An MSI is also produced for users/admins who prefer a conventional installer wizard.
# - Installed builds stay multi-file so Velopack delta packages remain small.

param(
    [string]$Version,
    [switch]$DownloadPrevious
)

$ErrorActionPreference = "Stop"

$InstallerDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root = Split-Path -Parent $InstallerDir
Set-Location $Root

if ([string]::IsNullOrWhiteSpace($Version)) {
    $csproj = Get-Content (Join-Path $Root "BrainFuel.csproj") -Raw
    if ($csproj -notmatch '<Version>([^<]+)</Version>') {
        throw "No <Version> found in BrainFuel.csproj"
    }
    $Version = $Matches[1].Trim()
}

$Rid = "win-x64"
$Channel = "win"
$RepoUrl = "https://github.com/turinglambdaai/brainfuel"
$PublishDir = Join-Path $Root "publish\velopack-$Rid"
$FeedDir = Join-Path $Root ".velopack-releases"
$DistDir = Join-Path $Root "dist\velopack"
$GeneratedDir = Join-Path $InstallerDir "generated"
$SplashPath = Join-Path $GeneratedDir "brainfuel-splash.png"
$LicensePath = Join-Path $GeneratedDir "LICENSE.txt"

function New-BrainFuelSplash([string]$Path) {
    Add-Type -AssemblyName System.Drawing
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null

    $bitmap = [System.Drawing.Bitmap]::new(640, 360)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

    $bg = [System.Drawing.Color]::FromArgb(31, 31, 30)
    $primary = [System.Drawing.Color]::FromArgb(240, 238, 230)
    $secondary = [System.Drawing.Color]::FromArgb(163, 158, 148)
    $accent = [System.Drawing.Color]::FromArgb(217, 119, 87)
    $accent2 = [System.Drawing.Color]::FromArgb(232, 212, 184)
    $track = [System.Drawing.Color]::FromArgb(58, 57, 55)
    $g.Clear($bg)

    $trackPen = [System.Drawing.Pen]::new($track, 14)
    $accentPen = [System.Drawing.Pen]::new($accent, 14)
    $accent2Pen = [System.Drawing.Pen]::new($accent2, 7)
    $trackPen.StartCap = $trackPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $accentPen.StartCap = $accentPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $accent2Pen.StartCap = $accent2Pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $g.DrawArc($trackPen, 58, 92, 126, 126, -90, 360)
    $g.DrawArc($accentPen, 58, 92, 126, 126, -90, 248)
    $g.DrawArc($accent2Pen, 77, 111, 88, 88, -90, 188)

    $titleFont = [System.Drawing.Font]::new("Segoe UI", 30, [System.Drawing.FontStyle]::Bold)
    $subtitleFont = [System.Drawing.Font]::new("Segoe UI", 13, [System.Drawing.FontStyle]::Regular)
    $smallFont = [System.Drawing.Font]::new("Segoe UI", 10, [System.Drawing.FontStyle]::Regular)
    $primaryBrush = [System.Drawing.SolidBrush]::new($primary)
    $secondaryBrush = [System.Drawing.SolidBrush]::new($secondary)
    $accentBrush = [System.Drawing.SolidBrush]::new($accent)

    $g.DrawString("BrainFuel", $titleFont, $primaryBrush, 220, 108)
    $g.DrawString("GLM Coding Plan quota, at a glance.", $subtitleFont, $secondaryBrush, 223, 158)
    $g.FillEllipse($accentBrush, 224, 203, 7, 7)
    $g.DrawString("Installing the desktop widget…", $smallFont, $secondaryBrush, 242, 196)
    $g.DrawString("Free & open source · GitHub Releases", $smallFont, $secondaryBrush, 223, 262)

    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)

    foreach ($item in @($trackPen, $accentPen, $accent2Pen, $titleFont, $subtitleFont, $smallFont, $primaryBrush, $secondaryBrush, $accentBrush, $g, $bitmap)) {
        $item.Dispose()
    }
}

Write-Host "== BrainFuel $Version Velopack package ($Rid) =="

dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed" }

if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
if (Test-Path $GeneratedDir) { Remove-Item $GeneratedDir -Recurse -Force }
New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null
New-Item -ItemType Directory -Path $FeedDir -Force | Out-Null
New-Item -ItemType Directory -Path $GeneratedDir -Force | Out-Null

New-BrainFuelSplash $SplashPath
Copy-Item (Join-Path $Root "LICENSE") $LicensePath -Force

# Multi-file publishing is deliberate: a small app change then touches only a
# few files, making Velopack delta packages substantially smaller than a full
# self-contained single-file executable.
dotnet publish BrainFuel.csproj -c Release -r $Rid --self-contained true `
    -p:Version=$Version `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

if ($DownloadPrevious) {
    Write-Host "== Downloading previous Velopack release for delta generation =="
    $downloadArgs = @(
        "tool", "run", "vpk", "--", "download", "github",
        "--repoUrl", $RepoUrl,
        "--outputDir", $FeedDir,
        "--channel", $Channel
    )
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
        $downloadArgs += @("--token", $env:GITHUB_TOKEN)
    }

    & dotnet @downloadArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "No previous Velopack feed could be downloaded; continuing with a full package."
    }
}

Write-Host "== Packaging branded Setup, optional MSI, full package, delta and update feed =="
$packArgs = @(
    "tool", "run", "vpk", "--", "pack",
    "--packId", "BrainFuel",
    "--packVersion", $Version,
    "--packDir", $PublishDir,
    "--mainExe", "BrainFuel.exe",
    "--packTitle", "BrainFuel",
    "--packAuthors", "TuringLambdaAI",
    "--outputDir", $FeedDir,
    "--runtime", $Rid,
    "--channel", $Channel,
    "--icon", (Join-Path $Root "Assets\tray.ico"),
    "--splashImage", $SplashPath,
    "--shortcuts", "StartMenuRoot",
    "--noPortable", "true",
    "--msi",
    "--instLocation", "PerUser",
    "--instWelcome", (Join-Path $InstallerDir "welcome.md"),
    "--instLicense", $LicensePath,
    "--instConclusion", (Join-Path $InstallerDir "conclusion.md")
)
& dotnet @packArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

if (Test-Path $DistDir) { Remove-Item $DistDir -Recurse -Force }
New-Item -ItemType Directory -Path $DistDir -Force | Out-Null

$full = Get-ChildItem $FeedDir -File | Where-Object { $_.Name -like "*-$Version-full.nupkg" } | Select-Object -First 1
if (-not $full) { throw "Velopack full package for $Version was not created" }
Copy-Item $full.FullName $DistDir

$delta = Get-ChildItem $FeedDir -File | Where-Object { $_.Name -like "*-$Version-delta.nupkg" } | Select-Object -First 1
if ($delta) { Copy-Item $delta.FullName $DistDir }

$setup = Get-ChildItem $FeedDir -File -Filter "*-Setup.exe" | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if (-not $setup) { throw "Velopack Setup.exe was not created" }
Copy-Item $setup.FullName (Join-Path $DistDir "BrainFuel-win-Setup.exe")

$msi = Get-ChildItem $FeedDir -File -Filter "*.msi" | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if (-not $msi) { throw "Velopack MSI was not created" }
Copy-Item $msi.FullName (Join-Path $DistDir "BrainFuel-win-Setup.msi")

$feed = Join-Path $FeedDir "releases.$Channel.json"
if (-not (Test-Path $feed)) { throw "Expected update feed $feed was not created" }
Copy-Item $feed $DistDir

Write-Host ""
Write-Host "Done. Release assets:"
Get-ChildItem $DistDir -File | ForEach-Object {
    Write-Host ("  {0} ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB))
}
