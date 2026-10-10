# Builds the self-contained fresh-PC installer that adds .NET 10 and PawnIO, then the framework-dependent app.

$ErrorActionPreference = 'Stop'
$root         = $PSScriptRoot
$app          = Join-Path $root 'FanControl.csproj'
$setup        = Join-Path $root 'Setup\Setup.csproj'
$appOut       = Join-Path $root 'dist'
$payloadDir   = Join-Path $root 'Setup\payload'
$installerOut = Join-Path $root 'installer'

Write-Host "1/3  Publishing the app (framework-dependent)..." -ForegroundColor Cyan
if (Test-Path $appOut) { Get-ChildItem $appOut -File | Remove-Item -Force }
dotnet publish $app -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:DebugSymbols=false -o $appOut
if ($LASTEXITCODE -ne 0) { throw "App publish failed." }

Write-Host "2/3  Staging the app as the installer payload..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force $payloadDir | Out-Null
Copy-Item (Join-Path $appOut 'TOA - Fan Control.exe') `
          (Join-Path $payloadDir 'TOA - Fan Control.exe') -Force

Write-Host "3/3  Publishing the installer (self-contained, runs on a bare PC)..." -ForegroundColor Cyan
if (Test-Path $installerOut) { Get-ChildItem $installerOut -File | Remove-Item -Force }
dotnet publish $setup -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o $installerOut
if ($LASTEXITCODE -ne 0) { throw "Installer publish failed." }

$exe = Join-Path $installerOut 'TOA - Fan Control Setup.exe'
$mb  = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Done. Installer: $exe  (~$mb MB)" -ForegroundColor Green
Write-Host "Share that ONE file for fresh PCs. It installs .NET + PawnIO (with prompts), then the app."
