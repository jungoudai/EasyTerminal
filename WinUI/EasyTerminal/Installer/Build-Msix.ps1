# Builds a signed sideload MSIX into Installer\Output\EasyTerminal_<version>_<platform>.msix.
# Usage: .\Build-Msix.ps1 -Version 1.0.10 [-Platform x64|x86|ARM64] [-Thumbprint <cert in CurrentUser\My>]
param(
    [string]$Version = "1.0.0",
    [ValidateSet('x64', 'x86', 'ARM64')] [string]$Platform = 'x64',
    [string]$Thumbprint
)

$ErrorActionPreference = "Stop"
$projectDir = Join-Path $PSScriptRoot "..\EasyTerminal"
$project = Join-Path $projectDir "EasyTerminal.csproj"
$manifest = Join-Path $projectDir "Package.appxmanifest"
$packages = Join-Path $projectDir "bin\AppPackages"
$output = Join-Path $PSScriptRoot "Output"

if (-not $Thumbprint) {
    # The signing subject must match Identity/@Publisher in Package.appxmanifest.
    $cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq 'CN=gouzu' -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) { throw "No 'CN=gouzu' signing certificate in Cert:\CurrentUser\My. Pass -Thumbprint." }
    $Thumbprint = $cert.Thumbprint
}

# MSIX versions have four parts; the manifest is stamped for this build and put back afterwards.
$parts = @($Version.Split('.'))
while ($parts.Count -lt 4) { $parts += '0' }
$packageVersion = ($parts | Select-Object -First 4) -join '.'
$original = [IO.File]::ReadAllText($manifest)
$stamped = [regex]::Replace($original, '(<Identity[^>]*?\sVersion=")[^"]*(")', "`${1}$packageVersion`${2}")

try {
    [IO.File]::WriteAllText($manifest, $stamped, [Text.UTF8Encoding]::new($true))
    if (Test-Path $packages) { Remove-Item $packages -Recurse -Force }

    $rid = "win-$($Platform.ToLower())"
    dotnet publish $project -c Release -p:Platform=$Platform -r $rid -p:Version=$Version `
        -p:GenerateAppxPackageOnBuild=true -p:AppxBundle=Never -p:UapAppxPackageBuildMode=SideloadOnly `
        -p:AppxPackageSigningEnabled=true -p:PackageCertificateThumbprint=$Thumbprint `
        -p:AppxPackageDir="$packages\" -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
}
finally {
    [IO.File]::WriteAllText($manifest, $original, [Text.UTF8Encoding]::new($true))
}

$msix = Get-ChildItem $packages -Recurse -Filter "EasyTerminal_*.msix" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msix) { throw "No MSIX was produced under $packages." }
New-Item -ItemType Directory -Force $output | Out-Null
$target = Join-Path $output "EasyTerminal_${packageVersion}_$Platform.msix"
Copy-Item $msix.FullName $target -Force
$target
