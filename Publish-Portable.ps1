# Builds the shareable, copy-anywhere version of TOA - Fan Control.
#
# Why self-contained: this bundles the entire .NET 10 runtime INTO the app folder,
# so it runs on any Windows PC even if that PC has never had .NET installed. The
# app can't install .NET for you (without .NET it can't even start), so the runtime
# has to ride along in the folder. PawnIO is different - the app installs that
# itself on first run.
#
# Output: the dist\ folder. Copy that whole folder to the other PC and run
# "TOA - Fan Control.exe" (as administrator). ~176 MB - the runtime is the bulk.

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'FanControl.csproj'
$out  = Join-Path $PSScriptRoot 'dist'

Write-Host "Publishing portable self-contained build..." -ForegroundColor Cyan

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false `
    -o $out

if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }

$exe = Join-Path $out 'TOA - Fan Control.exe'
$mb  = [math]::Round((Get-Item $exe).Length / 1MB)
Write-Host ""
Write-Host "Done. One portable file: $exe  (~$mb MB)" -ForegroundColor Green
Write-Host "Copy that ONE .exe to the other PC, then run it as admin. That's the whole app."
