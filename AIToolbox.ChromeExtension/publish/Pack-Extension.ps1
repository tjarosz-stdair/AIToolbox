#Requires -Version 7.0
<#
.SYNOPSIS
  Builds and packs the AIToolbox Chrome extension into a signed .crx plus its own
  update_manifest.xml, ready for AIToolbox.ChromeExtension.Publish (MSBuild) to push to
  chromeextensions.stdair.com.

.DESCRIPTION
  Does NOT bump manifest.json/package.json's version -- that stays the existing manual habit
  (bump both together before running this). This script only packages whatever version is
  already set in manifest.json.

  Never regenerate aitoolbox-extension.pem. Chrome derives the extension's permanent ID from
  that key's public half -- a new key silently mints a different ID and breaks auto-update for
  every copy already installed. See README.md.
#>
param(
    [string]$ExtensionRoot = (Join-Path $PSScriptRoot ".."),
    [string]$OutputDir = (Join-Path $PSScriptRoot "output"),
    [string]$KeyPath = (Join-Path $PSScriptRoot "aitoolbox-extension.pem"),
    [string]$CodebaseUrl = "https://chromeextensions.stdair.com/aitoolbox.crx",
    [string]$ExtensionId = "lmhmnihpflkpmaojclcnjodbggfndoka"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $KeyPath)) {
    throw "Signing key not found at '$KeyPath'. This key is permanent and generated exactly once -- see publish/README.md. Recover the existing key (from wherever it was backed up); do not generate a new one."
}

$manifestPath = Join-Path $ExtensionRoot "manifest.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$version = $manifest.version
if (-not $version) {
    throw "Could not read a version from $manifestPath."
}
Write-Host "Packing AIToolbox extension version $version (appid=$ExtensionId)..."

# 1. Build (existing esbuild script -- package.json's "build")
Push-Location $ExtensionRoot
try {
    npm ci
    npm run build
} finally {
    Pop-Location
}

# 2. Stage exactly what "Load unpacked" uses today: everything at the extension root except
# dev-only files. An EXCLUDE list rather than an include list, deliberately -- an include list
# silently drops a newly added html/css/asset file (this extension has already grown several
# such files this project), whereas an exclude list only needs updating when a new *dev-only*
# item shows up, which is rare and obvious.
$stageDir = Join-Path $PSScriptRoot "stage"
if (Test-Path $stageDir) {
    Remove-Item $stageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stageDir | Out-Null

$excludeDirs = @("node_modules", "src", "publish", ".git")
$excludeFiles = @("package.json", "package-lock.json", "tsconfig.json", ".gitignore")

Get-ChildItem -Path $ExtensionRoot -Force | ForEach-Object {
    if ($_.PSIsContainer) {
        if ($excludeDirs -notcontains $_.Name) {
            Copy-Item $_.FullName -Destination (Join-Path $stageDir $_.Name) -Recurse
        }
    }
    elseif ($excludeFiles -notcontains $_.Name) {
        Copy-Item $_.FullName -Destination (Join-Path $stageDir $_.Name)
    }
}

# 3. Locate an installed Chrome or Edge and pack via its --pack-extension CLI.
$browserCandidates = @(
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
)
$browser = $browserCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) {
    throw "No installed Chrome or Edge found in the usual install paths. Install one, or pass a different path via `$browserCandidates` in this script."
}

Write-Host "Packing with $browser..."
& $browser --pack-extension="$stageDir" --pack-extension-key="$KeyPath"

# Confirmed empirically: chrome.exe hands the pack request off to a background process and
# returns almost immediately -- the .crx lands on disk a moment AFTER the CLI call already
# exited, not synchronously with it. Poll rather than checking right away.
$producedCrx = Join-Path $PSScriptRoot "stage.crx"
$deadline = (Get-Date).AddSeconds(20)
while (-not (Test-Path $producedCrx) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
}
if (-not (Test-Path $producedCrx)) {
    throw "Expected packed output at '$producedCrx' but it wasn't produced within 20s. Check the browser's output above -- --pack-extension support has shifted across Chrome versions, so this is exactly the kind of thing the plan's local dry run is meant to catch before this ever runs against the real site."
}

# 4. Final output: exactly the 2 files AIToolbox.ChromeExtension.Publish will push.
if (Test-Path $OutputDir) {
    Remove-Item $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDir | Out-Null

Move-Item $producedCrx (Join-Path $OutputDir "aitoolbox.crx") -Force

# This file is exclusively AIToolbox's own -- never the shared update_manifest.xml the Dayforce
# extension already uses -- so regenerating it from scratch each time is safe.
$manifestXml = @"
<?xml version="1.0" encoding="UTF-8"?>
<gupdate xmlns="http://www.google.com/update2/response" protocol="2.0">
  <app appid="$ExtensionId">
    <updatecheck codebase="$CodebaseUrl" version="$version" />
  </app>
</gupdate>
"@
Set-Content -Path (Join-Path $OutputDir "aitoolbox_update_manifest.xml") -Value $manifestXml -Encoding UTF8

Remove-Item $stageDir -Recurse -Force

Write-Host "Packed AIToolbox v$version -> $OutputDir"
Write-Host "  aitoolbox.crx"
Write-Host "  aitoolbox_update_manifest.xml (appid=$ExtensionId, codebase=$CodebaseUrl)"
