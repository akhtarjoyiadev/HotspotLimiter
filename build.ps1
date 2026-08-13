# Build script for HotspotLimiter Standalone Release
$ErrorActionPreference = 'Stop'

Write-Host "Building HotspotLimiter Standalone Release (win-x64)..." -ForegroundColor Cyan

$outputDir = Join-Path $PSScriptRoot "publish"
if (Test-Path $outputDir) {
    Remove-Item -Recurse -Force $outputDir
}

$env:DOTNET_ROOT = "C:\Users\innfo\.dotnet"
C:\Users\innfo\.dotnet\dotnet.exe publish (Join-Path $PSScriptRoot "HotspotLimiter.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o "$outputDir"

Write-Host "`nBuild succeeded! Output directory: $outputDir" -ForegroundColor Green
Write-Host "NOTE: Copy WinDivert.dll and WinDivert64.sys into '$outputDir' before running." -ForegroundColor Yellow
