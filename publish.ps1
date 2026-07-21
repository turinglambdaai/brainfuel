# Publish a self-contained single-file exe (no .NET install needed on the target).
# Usage:  ./publish.ps1               # win-x64 (default)
#         ./publish.ps1 osx-arm64     # macOS Apple Silicon
#         ./publish.ps1 linux-x64     # Linux
param(
    [string]$Rid = "win-x64"
)

$out = "publish/$Rid"
dotnet publish -c Release -r $Rid --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    -o $out

if ($LASTEXITCODE -eq 0) {
    Write-Output ""
    Write-Output "Published -> $out/BrainFuel(.exe)"
}
