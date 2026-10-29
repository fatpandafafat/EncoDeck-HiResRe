# SPDX-License-Identifier: GPL-3.0-only
<# Builds the optional x64 LHDC component from a fresh upstream checkout of the commit pinned in
   src/EncoDeck.Platform/Components/components.json (or -Commit), on EVERY invocation.
   The component is versioned on its own (e.g. 0.8.8-r1), not with the app.
   Does not install drivers or change Windows boot/security settings. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Destination,
    [string] $PowerShellDirectory,
    [string] $ArchivePath,
    [string] $ComponentVersion,
    [string] $Commit
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$pin = (Get-Content (Join-Path $root 'src/EncoDeck.Platform/Components/components.json') -Raw | ConvertFrom-Json).components | Where-Object id -eq 'lhdc'
if (-not $ComponentVersion) { $ComponentVersion = $pin.version }
if (-not $Commit) { $Commit = $pin.source.commit }
if ($ComponentVersion -notmatch '^\d+\.\d+\.\d+-r\d+$') { throw "A component version like 0.8.8-r1 is required, not '$ComponentVersion'." }
if ($Commit -notmatch '^[0-9a-f]{40}$') { throw 'A full 40-digit upstream commit is required.' }
$destinationPath = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $destinationPath) { throw "Destination must not already exist: $destinationPath" }
$repository = 'https://github.com/Wayne-u/LHDC-Win.git'
# A unique checkout prevents stale binaries or a modified local remote from being reused.
$source = Join-Path $root ('artifacts/lhdc/sources/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Split-Path -Parent $source) -Force | Out-Null
function Invoke-GitChecked([string[]] $Arguments) {
    & git @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Upstream git operation failed: $($Arguments -join ' ')" }
}
Invoke-GitChecked @('clone', '--branch', 'main', '--single-branch', $repository, $source)
# The pinned commit, never whatever main is today: a component version always means the same source.
Invoke-GitChecked @('-C', $source, 'checkout', '--detach', $Commit)
# Only initialize the two pinned dependencies used by the upstream build.
Invoke-GitChecked @('-C', $source, 'submodule', 'update', '--init', '--', 'third_party/lhdcv5', 'third_party/windows-driver-samples')
$commit = & git -C $source rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $commit -ne $Commit) { throw 'Cannot check out the pinned upstream revision.' }
Write-Host "Building LHDC-Win from $repository @ $commit"
$kits = Get-ItemProperty -LiteralPath 'HKLM:/SOFTWARE/Microsoft/Windows Kits/Installed Roots' -ErrorAction SilentlyContinue
if (-not $kits -or -not (Test-Path -LiteralPath (Join-Path $kits.KitsRoot10 'Include/10.0.28000.0/km'))) {
    throw "Install SDK/WDK 28000 and KMDF 1.33 as required by upstream. Source retained at $source"
}
foreach ($target in @('Host', 'Transport', 'Audio')) {
    & (Join-Path $source 'scripts/Build.ps1') -Target $target -Configuration Release
    if (-not $?) { throw "LHDC $target build failed." }
}
& (Join-Path $source 'scripts/Sign-TestPackage.ps1')
if (-not $?) { throw 'LHDC transport signing failed.' }
& (Join-Path $source 'scripts/Sign-TestPackage.ps1') -Audio
if (-not $?) { throw 'LHDC audio signing failed.' }
if (-not $PowerShellDirectory) {
    $PowerShellDirectory = (Get-ItemProperty 'HKLM:/SOFTWARE/Microsoft/PowerShellCore/InstalledVersions/31ab5147-9a97-4452-8443-d9709f0516e1').InstallLocation
}
$PowerShellDirectory = (Resolve-Path -LiteralPath $PowerShellDirectory).Path
foreach ($name in @('pwsh.exe','pwsh.runtimeconfig.json','LICENSE.txt','ThirdPartyNotices.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PowerShellDirectory $name))) { throw "Incomplete PowerShell runtime: $name" }
}
$runtimeInfo = & (Join-Path $PowerShellDirectory 'pwsh.exe') -NoProfile -Command '$PSVersionTable.PSVersion.ToString(); [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()'
if ($LASTEXITCODE -ne 0 -or $runtimeInfo.Count -ne 2 -or ([version]$runtimeInfo[0]).Major -ne 7 -or $runtimeInfo[1] -ne 'X64') { throw 'PowerShell 7 x64 is required.' }
$runtimeConfig = Get-Content (Join-Path $PowerShellDirectory 'pwsh.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.PSObject.Properties.Name -contains 'framework' -or $runtimeConfig.runtimeOptions.PSObject.Properties.Name -contains 'frameworks') { throw 'A self-contained PowerShell runtime is required.' }
$status = @(& git -C $source status --porcelain)
if ($LASTEXITCODE -ne 0 -or $status.Count) { throw 'LHDC source must remain clean.' }
$package = Join-Path $source 'build/encodeck-runtime'
foreach ($directory in @('backend','drivers/transport','drivers/audio','scripts','runtime/powershell','licenses')) {
    New-Item -ItemType Directory -Path (Join-Path $package $directory) -Force | Out-Null
}
foreach ($name in @('lhdc-host','lhdc-service','lhdc-device','lhdc-audio-device')) {
    $program = Join-Path $source "build/host/$name.exe"
    $dependencies = & dumpbin.exe /dependents $program
    if ($LASTEXITCODE -ne 0 -or $dependencies -match '(?i)\b(?:msvcp\d+\w*|vcruntime\d+\w*|msvcr\d+|ucrtbase)\.dll\b') { throw "Native binary must use static runtime: $name" }
    Copy-Item -LiteralPath $program -Destination (Join-Path $package 'backend')
}
foreach ($driver in @(@{Name='lhdc-transport';Source='build/test-package';Destination='drivers/transport';Verifier='lhdc-device'},@{Name='lhdc-audio';Source='build/test-audio-package';Destination='drivers/audio';Verifier='lhdc-audio-device'})) {
    foreach ($extension in @('sys','inf','cat')) {
        Copy-Item -LiteralPath (Join-Path $source ($driver.Source+'/'+$driver.Name+'.'+$extension)) -Destination (Join-Path $package $driver.Destination)
    }
    & (Join-Path $package ('backend/'+$driver.Verifier+'.exe')) verify-package (Join-Path $package $driver.Destination) (Join-Path $source 'build/test-certificate/lhdc-test.cer')
    if ($LASTEXITCODE -ne 0) { throw "Driver verification failed: $($driver.Name)" }
}
Copy-Item -LiteralPath (Join-Path $source 'build/test-certificate/lhdc-test.cer') -Destination (Join-Path $package 'drivers/lhdc-test.cer')
Get-ChildItem -LiteralPath $PowerShellDirectory -Force | Copy-Item -Destination (Join-Path $package 'runtime/powershell') -Recurse
Copy-Item -LiteralPath (Join-Path $source 'scripts/Set-AudioMode.ps1') -Destination (Join-Path $package 'scripts')
Copy-Item -LiteralPath (Join-Path $source 'LICENSE'),(Join-Path $source 'NOTICE.md') -Destination $package
Get-ChildItem -LiteralPath (Join-Path $source 'licenses') -File | Copy-Item -Destination (Join-Path $package 'licenses')
Copy-Item -LiteralPath (Join-Path $PowerShellDirectory 'LICENSE.txt') -Destination (Join-Path $package 'licenses/PowerShell-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $PowerShellDirectory 'ThirdPartyNotices.txt') -Destination (Join-Path $package 'licenses/PowerShell-ThirdPartyNotices.txt')
[ordered]@{Schema=2;Component='lhdc';ComponentVersion=$ComponentVersion;SourceRepository='https://github.com/Wayne-u/LHDC-Win';Architecture='x64';Interface='EncoDeck-native';SourceCommit=$commit} | ConvertTo-Json | Set-Content (Join-Path $package 'runtime.json') -Encoding UTF8
[ordered]@{ComponentVersion=$ComponentVersion;SourceCommit=$commit;SourceTreeDirty=$false;BuiltAt=Get-Date -Format o;Architecture='x64';MinimumWindowsBuild=22621;DriverSigning='Test';PowerShellVersion=$runtimeInfo[0];SelfContainedUI=$false} | ConvertTo-Json | Set-Content (Join-Path $package 'release.json') -Encoding UTF8
$hashes = foreach ($file in Get-ChildItem -LiteralPath $package -Recurse -File) {
    [pscustomobject]@{Path=[IO.Path]::GetRelativePath($package,$file.FullName).Replace('\','/');SHA256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
}
$hashes | ConvertTo-Json | Set-Content (Join-Path $package 'checksums.json') -Encoding UTF8
New-Item -ItemType Directory -Path (Split-Path -Parent $destinationPath) -Force | Out-Null
Copy-Item -LiteralPath $package -Destination $destinationPath -Recurse
if ($ArchivePath) {
    $archive = [IO.Path]::GetFullPath($ArchivePath)
    if (Test-Path -LiteralPath $archive) { throw "Archive already exists: $archive" }
    New-Item -ItemType Directory -Path (Split-Path -Parent $archive) -Force | Out-Null
    [IO.Compression.ZipFile]::CreateFromDirectory($destinationPath, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
    ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash + '  ' + [IO.Path]::GetFileName($archive)) | Set-Content ($archive+'.sha256') -Encoding ascii
}
Write-Host "LHDC component ${ComponentVersion}: $destinationPath (upstream $commit)"
