param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'publish-reliable'))
$ErrorActionPreference = 'Stop'
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (!(Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_ROOT = Split-Path $dotnet
& $dotnet publish (Join-Path $PSScriptRoot 'HotspotLimiter.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE)." }
Write-Host "Built: $OutputDirectory\HotspotLimiter.exe (native files are embedded)." -ForegroundColor Green
Write-Host 'Run as Administrator. Existing build directories are preserved.'
