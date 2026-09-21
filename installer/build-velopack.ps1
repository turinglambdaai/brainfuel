# Build a Windows Velopack installer and update feed.
#
# - The installed build is self-contained but intentionally NOT single-file so
#   delta packages stay small when only BrainFuel assemblies change.
# - Previous release metadata/package can be downloaded before packing so vpk
#   can produce a delta package.
# - The local tool manifest pins vpk to the same version as the Velopack NuGet
#   package referenced by BrainFuel.csproj.

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

Write-Host "== BrainFuel $Version Velopack package ($Rid) =="

# Pin the packaging CLI through .config/dotnet-tools.json.
dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed" }

if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null
New-Item -ItemType Directory -Path $FeedDir -Force | Out-Null

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
        # Expected for the first Velopack-enabled release because older BrainFuel
        # releases have no releases.win.json feed yet. Packing a full release is
        # still valid; the next release will be able to generate a delta.
        Write-Warning "No previous Velopack feed could be downloaded; continuing with a full package."
    }
}

Write-Host "== Packaging installer, full package, delta and update feed =="
& dotnet tool run vpk -- pack `
    --packId BrainFuel `
    --packVersion $Version `
    --packDir $PublishDir `
    --mainExe BrainFuel.exe `
    --packTitle BrainFuel `
    --packAuthors TuringLambdaAI `
    --outputDir $FeedDir `
    --runtime $Rid `
    --channel $Channel `
    --icon (Join-Path $Root "Assets\tray.ico") `
    --shortcuts StartMenuRoot `
    --noPortable true
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

if (Test-Path $DistDir) { Remove-Item $DistDir -Recurse -Force }
New-Item -ItemType Directory -Path $DistDir -Force | Out-Null

$full = Get-ChildItem $FeedDir -File | Where-Object {
    $_.Name -like "*-$Version-full.nupkg"
} | Select-Object -First 1
if (-not $full) { throw "Velopack full package for $Version was not created" }
Copy-Item $full.FullName $DistDir

$delta = Get-ChildItem $FeedDir -File | Where-Object {
    $_.Name -like "*-$Version-delta.nupkg"
} | Select-Object -First 1
if ($delta) { Copy-Item $delta.FullName $DistDir }

$setup = Get-ChildItem $FeedDir -File -Filter "*-Setup.exe" |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if (-not $setup) { throw "Velopack Setup.exe was not created" }
Copy-Item $setup.FullName $DistDir

$feed = Join-Path $FeedDir "releases.$Channel.json"
if (-not (Test-Path $feed)) { throw "Expected update feed $feed was not created" }
Copy-Item $feed $DistDir

Write-Host ""
Write-Host "Done. Release assets:"
Get-ChildItem $DistDir -File | ForEach-Object {
    Write-Host ("  {0} ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB))
}
