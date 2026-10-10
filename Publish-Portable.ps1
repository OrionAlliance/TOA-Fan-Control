# Builds the portable exe framework-dependent on purpose, so it runs on the PC's shared .NET 10, which the app keeps patched.

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'FanControl.csproj'
$out  = Join-Path $PSScriptRoot 'dist'

Write-Host "Publishing framework-dependent build (uses the PC's .NET 10)..." -ForegroundColor Cyan

if (Test-Path $out) { Get-ChildItem $out -File | Remove-Item -Force }

dotnet publish $proj -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:DebugSymbols=false `
    -o $out

if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }

$exe = Join-Path $out 'TOA - Fan Control.exe'
$mb  = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Done. One file: $exe  (~$mb MB)" -ForegroundColor Green
Write-Host "Copy that .exe to the other PC and run it as admin. On a PC without .NET 10,"
Write-Host "Windows will prompt to install it first (one click)."
