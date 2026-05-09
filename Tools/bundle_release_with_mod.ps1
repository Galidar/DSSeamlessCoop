<#
.SYNOPSIS
  Inject the DS2 Multiplayer Overhaul mod into a published Bonfire release ZIP.

.DESCRIPTION
  CI publishes a "bare" windows.zip on every tag push (no DS2 mod data — that
  cannot live in the public git repo because it includes data derived from the
  retail Dark Souls II files). This script runs locally after CI finishes and:

    1. Downloads windows.zip from the release via `gh`.
    2. Extracts it.
    3. Copies <ModRoot>/* into  DSSeamlessCoop/Loader/ds2multoverhaul/
       (skipping DSMapStudio/, project.json, *.prev — editor metadata).
    4. Copies the companion modengine.ini into DSSeamlessCoop/Loader/.
    5. Re-zips.
    6. Replaces the windows.zip asset on the GitHub release (--clobber).

  After this, a user has exactly one download to grab from the release page —
  Bonfire + DS2 Overhaul in a single ZIP, ready to extract and run.

.PARAMETER Tag
  Release tag, e.g. v0.4.0.

.PARAMETER ModRoot
  Path to the ds2multoverhaul folder (the inner one with Param\, map\, menu\,
  enc_regulation.bnd.dcx). modengine.ini is expected one directory above.
  If omitted, falls back to the DS2_OVERHAUL_DIR environment variable.

.PARAMETER WorkDir
  Scratch directory for extract / repack. Default: %TEMP%\bonfire-bundle-<Tag>.

.EXAMPLE
  .\Tools\bundle_release_with_mod.ps1 -Tag v0.4.0 -ModRoot C:\path\to\ds2multoverhaul

.EXAMPLE
  $env:DS2_OVERHAUL_DIR = 'C:\path\to\ds2multoverhaul'
  .\Tools\bundle_release_with_mod.ps1 -Tag v0.4.0
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$Tag,

    [string]$ModRoot,

    [string]$WorkDir
)

$ErrorActionPreference = 'Stop'

if (-not $ModRoot) {
    $ModRoot = $env:DS2_OVERHAUL_DIR
}
if (-not $ModRoot) {
    throw "ModRoot is required. Pass -ModRoot <path> or set `$env:DS2_OVERHAUL_DIR. " +
          "It must point to the ds2multoverhaul folder containing Param\, map\, menu\, " +
          "and enc_regulation.bnd.dcx, with modengine.ini one level above."
}
if (-not (Test-Path $ModRoot)) {
    throw "ModRoot not found: $ModRoot"
}
$ModEngineIni = Join-Path (Split-Path $ModRoot -Parent) 'modengine.ini'
if (-not (Test-Path $ModEngineIni)) {
    throw "modengine.ini not found alongside ModRoot: $ModEngineIni"
}

if (-not $WorkDir) {
    $WorkDir = Join-Path $env:TEMP "bonfire-bundle-$Tag"
}
if (Test-Path $WorkDir) { Remove-Item $WorkDir -Recurse -Force }
New-Item -ItemType Directory -Path $WorkDir | Out-Null
Write-Output "Work dir: $WorkDir"

Write-Output ""
Write-Output "Downloading windows.zip from release $Tag ..."
$DownloadDir = Join-Path $WorkDir 'download'
New-Item -ItemType Directory -Path $DownloadDir | Out-Null
& gh release download $Tag --pattern 'windows.zip' --dir $DownloadDir
if ($LASTEXITCODE -ne 0) { throw "gh release download failed for tag $Tag" }
$ZipPath = Join-Path $DownloadDir 'windows.zip'
$DownloadedSize = (Get-Item $ZipPath).Length
Write-Output ("Downloaded: {0} ({1:N0} bytes, {2:N1} MB)" -f `
    $ZipPath, $DownloadedSize, ($DownloadedSize / 1MB))

Write-Output ""
Write-Output "Extracting ..."
$ExtractDir = Join-Path $WorkDir 'extracted'
Expand-Archive -Path $ZipPath -DestinationPath $ExtractDir -Force
$LoaderDir = Join-Path $ExtractDir 'DSSeamlessCoop\Loader'
if (-not (Test-Path $LoaderDir)) {
    throw "Expected DSSeamlessCoop\Loader\ inside zip, not found"
}

Write-Output ""
Write-Output "Injecting mod into Loader\ds2multoverhaul (excluding editor metadata) ..."
$DestModDir = Join-Path $LoaderDir 'ds2multoverhaul'
& robocopy $ModRoot $DestModDir /E /XD DSMapStudio /XF project.json '*.prev' /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) {
    throw "robocopy failed (exit code $LASTEXITCODE)"
}
$ModSize = (Get-ChildItem $DestModDir -Recurse | Measure-Object -Property Length -Sum).Sum
Write-Output ("Mod injected: {0:N1} MB" -f ($ModSize / 1MB))

Copy-Item $ModEngineIni (Join-Path $LoaderDir 'modengine.ini') -Force
Write-Output "Copied modengine.ini"

Write-Output ""
Write-Output "Repacking windows.zip ..."
$NewZip = Join-Path $WorkDir 'windows.zip'
if (Test-Path $NewZip) { Remove-Item $NewZip -Force }
Compress-Archive -Path (Join-Path $ExtractDir 'DSSeamlessCoop') -DestinationPath $NewZip
$NewSize = (Get-Item $NewZip).Length
Write-Output ("Repacked: {0} ({1:N0} bytes, {2:N1} MB)" -f `
    $NewZip, $NewSize, ($NewSize / 1MB))

Write-Output ""
Write-Output "Uploading windows.zip back to release $Tag (--clobber) ..."
& gh release upload $Tag $NewZip --clobber
if ($LASTEXITCODE -ne 0) { throw "gh release upload failed" }

Write-Output ""
Write-Output "Done. Release $Tag now has one windows.zip containing Bonfire + DS2 Overhaul."
