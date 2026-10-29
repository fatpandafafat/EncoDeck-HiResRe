# SPDX-License-Identifier: GPL-3.0-only
<#
.SYNOPSIS
  Builds EncoDeck and produces the Windows installer (EncoDeck-Setup-<version>-win-x64.exe).

.DESCRIPTION
  1. Runs the unit tests (unless -SkipTests).
  2. Publishes the WinUI app self-contained for win-x64 into artifacts\EncoDeck-win-x64.
  3. Downloads the Inno Setup compiler and builds the installer.
  Device images and animations are downloaded by the installed app through EncoAPI.

.EXAMPLE
  pwsh build\build-installer.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = "Release",
    [string] $Version,
    [switch] $SkipTests,
    [switch] $WithLhdc,
    [string] $LhdcPowerShellDirectory
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Version) {
    $props = Get-Content (Join-Path $root "Directory.Build.props") -Raw
    $Version = [regex]::Match($props, "<VersionPrefix>([^<]+)</VersionPrefix>").Groups[1].Value
}
$numeric = ($Version -split "[-+]")[0]
while (($numeric -split "\.").Count -lt 4) { $numeric += ".0" }
Write-Host "EncoDeck $Version ($numeric)"

if (-not $SkipTests) {
    dotnet test tests/EncoDeck.Tests -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed." }
}

$publish = Join-Path $root "artifacts\EncoDeck-win-x64"
# Replacing the folder under a running copy would delete only the files it does not lock.
$running = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith("$publish\", [StringComparison]::OrdinalIgnoreCase) }
if ($running) { throw "EncoDeck is running from $publish (PID $($running.Id -join ', ')). Close it first; the build replaces that folder." }
$publish = [IO.Path]::GetFullPath($publish)
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $root "artifacts")) + [IO.Path]::DirectorySeparatorChar
if (-not $publish.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Unexpected publish directory: $publish" }
if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
dotnet publish src/EncoDeck.App -c $Configuration -r win-x64 -p:Platform=x64 `
    -p:VersionPrefix=$((($Version -split "[-+]")[0])) -p:VersionSuffix="$(if ($Version -match '-(.+)$') { $Matches[1] } else { '' })" `
    -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

foreach ($required in "EncoDeck.exe", "EncoDeck.pri", "Microsoft.WindowsAppRuntime.dll", "Microsoft.ui.xaml.dll", "LICENSE", "THIRD_PARTY_NOTICES.md", "Assets\EncoDeck.ico", "Assets\EncoDeck-48.png") {
    if (-not (Test-Path (Join-Path $publish $required))) { throw "Publish output is missing $required." }
}
Copy-Item README.md, README_EN.md $publish -ErrorAction SilentlyContinue

if ($WithLhdc) {
    # The pinned LHDC component (its own version, not the app's), for an offline installer.
    & (Join-Path $PSScriptRoot 'build-lhdc.ps1') -Destination (Join-Path $publish 'LHDC') -PowerShellDirectory $LhdcPowerShellDirectory
    if (-not $?) { throw 'LHDC component build failed.' }
}

dotnet restore installer/InnoSetup.Tool.csproj --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Could not download the Inno Setup compiler." }
$innoVersion = [regex]::Match((Get-Content installer/InnoSetup.Tool.csproj -Raw), "<InnoSetupVersion>([^<]+)</InnoSetupVersion>").Groups[1].Value
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget\packages" }
$iscc = Join-Path $nugetRoot "tools.innosetup\$innoVersion\tools\ISCC.exe"
if (-not (Test-Path $iscc)) { throw "ISCC.exe not found at $iscc" }

$output = Join-Path $root "artifacts\installer"
& $iscc "/DAppVersion=$Version" "/DAppVersionNumeric=$numeric" "/DSourceDir=$publish" "/DOutputDir=$output" "/Qp" installer\EncoDeck.iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }

$setup = Get-ChildItem $output -Filter "EncoDeck-Setup-$Version-win-x64.exe" | Select-Object -First 1
$hash = (Get-FileHash $setup.FullName -Algorithm SHA256).Hash
"{0}  {1}" -f $hash, $setup.Name | Set-Content (Join-Path $output "$($setup.Name).sha256") -Encoding ascii
Write-Host ("Installer: {0} ({1:N1} MB)`nSHA-256:   {2}" -f $setup.FullName, ($setup.Length / 1MB), $hash)
