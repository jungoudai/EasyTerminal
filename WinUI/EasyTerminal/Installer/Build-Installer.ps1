# Publishes a self-contained x64 build and wraps it in Installer\Output\EasyTerminal-Setup-<version>-x64.exe.
param([string]$Version = "1.0.0")

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\EasyTerminal\EasyTerminal.csproj"
$publish = Join-Path $PSScriptRoot "..\EasyTerminal\bin\Installer\publish"

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 was not found." }

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

dotnet publish $project -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:InstallerBuild=true -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

& $iscc "/DAppVersion=$Version" "/DPublishDir=$publish" (Join-Path $PSScriptRoot "EasyTerminal.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }
