# Builds dist\RescueDesk-Setup-<version>.exe
# Requires the .NET 10 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
param([string]$Version = "0.0.0-local")
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (Test-Path publish) { Remove-Item publish -Recurse -Force }
dotnet publish RescueDesk.csproj -c Release -r win-x64 --self-contained true -o publish -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

& $iscc "/DAppVersion=$Version" installer\RescueDesk.iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
Get-ChildItem "dist\RescueDesk-Setup-$Version.exe"
