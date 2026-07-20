# Builds the shareable version of TOA - Fan Control.
#
# This is FRAMEWORK-DEPENDENT on purpose: the app uses the .NET 10 that's installed
# on the PC rather than carrying its own copy. Why it matters - when Windows Update
# patches .NET 10 (security/performance fixes), the app automatically runs on the
# patched version next launch. A self-contained build would freeze one .NET version
# inside the exe and never get those fixes.
#
# Result: one small .exe (~a few MB, not 74). It needs .NET 10 present:
#   - Your PCs already have it, so they just run it.
#   - A brand-new PC that lacks it: Windows shows a "get .NET" prompt with the exact
#     download link the moment the exe is run (one click, one time).
# PawnIO is still installed/updated by the app itself on first run.

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
