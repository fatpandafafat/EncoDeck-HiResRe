# SPDX-License-Identifier: GPL-3.0-only
<#
.SYNOPSIS
  Builds the LHDC component pinned in components.json and (with -Publish) uploads it to its own
  component release (lhdc-v<version>), never to an app release.

.DESCRIPTION
  The component is built from the pinned LHDC-Win commit and named after the component version
  (EncoDeck-LHDC-<version>-win-x64.zip). Its size and SHA-256 are written to component-pin.json next
  to the archive: copy them into src/EncoDeck.Platform/Components/components.json, which is what the
  app accepts. Each build produces new bytes (fresh test certificate), so a published version is never
  rebuilt or overwritten; bump the -r<n> revision instead.
#>
[CmdletBinding()]
param([string]$PowerShellDirectory, [string]$OutputDirectory, [switch]$Publish)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$pin = (Get-Content (Join-Path $root 'src/EncoDeck.Platform/Components/components.json') -Raw | ConvertFrom-Json).components | Where-Object id -eq 'lhdc'
$version = $pin.version
$tag = $pin.tag
$file = "EncoDeck-LHDC-$version-win-x64.zip"
if ($pin.packages[0].file -ne $file) { throw "components.json names $($pin.packages[0].file), expected $file." }
if ($tag -ne "lhdc-v$version") { throw "The LHDC tag must be lhdc-v$version, not $tag." }
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root ('artifacts/lhdc-runtime/' + [guid]::NewGuid().ToString('N')) }
$archive = Join-Path $output $file
& (Join-Path $PSScriptRoot 'build-lhdc.ps1') -Destination (Join-Path $output 'runtime') -ArchivePath $archive -PowerShellDirectory $PowerShellDirectory `
    -ComponentVersion $version -Commit $pin.source.commit
if (-not $?) { throw 'LHDC component build failed.' }
$item = Get-Item -LiteralPath $archive
$sha = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[ordered]@{ id = 'lhdc'; version = $version; tag = $tag; file = $file; size = $item.Length; sha256 = $sha; commit = $pin.source.commit } |
    ConvertTo-Json | Set-Content (Join-Path $output 'component-pin.json') -Encoding utf8
# What apps released earlier read to find this component without an app update.
$manifest = & (Join-Path $PSScriptRoot 'component-manifest.ps1') -Id lhdc -OutputDirectory $output -Archive $archive
if ($Publish) {
    & gh release view $tag --repo TIANLI0/EncoDeck *> $null
    if ($LASTEXITCODE -eq 0) { throw "Component release $tag exists already; publish a new revision instead of replacing it." }
    $notes = "LHDC component $version, compiled from LHDC-Win $($pin.source.release) ($($pin.source.repository)/commit/$($pin.source.commit)).`n`nSHA-256: $sha"
    # A component release is never the repository's latest release: that stays the newest app.
    & gh release create $tag $archive ($archive + '.sha256') $manifest --repo TIANLI0/EncoDeck --title "LHDC component $version" --notes $notes --latest=false
    if ($LASTEXITCODE -ne 0) { throw 'Could not publish the LHDC component release.' }
}
Write-Host "LHDC component $version : $archive ($($item.Length) bytes, sha256 $sha)"
