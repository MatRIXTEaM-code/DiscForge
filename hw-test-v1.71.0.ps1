# DiscForge v1.71.0 -- hardware test: drive-capabilities profile (with cache-defeat probe)
# and Tier-B adaptive re-read, both against the real PX-W5224TA.
#
# Usage:
#   .\hw-test-v1.71.0.ps1
#   .\hw-test-v1.71.0.ps1 -Drive E -Rebuild
#   .\hw-test-v1.71.0.ps1 -Lba 12345    # probe a specific sector for reread-probe instead of track start

param(
    [string]$Drive = "D",
    [long]$Lba = -1,
    [switch]$Rebuild
)

$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

Write-Host "=== DiscForge v1.71.0 hardware test: drive-profile + reread-probe ===" -ForegroundColor Cyan

if ($Rebuild) {
    Write-Host "`n-- Rebuild requested --" -ForegroundColor Yellow
    & "$PSScriptRoot\build.ps1" -Rebuild
    if ($LASTEXITCODE -ne 0) { Write-Error "build.ps1 failed with exit code $LASTEXITCODE"; exit 1 }
}

$dforge = $null
$candidates = @(
    "$PSScriptRoot\src\DiscForge.Cli\bin\Release\net8.0-windows\dforge.exe",
    "$PSScriptRoot\src\DiscForge.Cli\bin\Debug\net8.0-windows\dforge.exe",
    "C:\tools\dforge\dforge.exe"
)
foreach ($c in $candidates) { if (Test-Path $c) { $dforge = $c; break } }
if (-not $dforge) {
    $found = Get-ChildItem -Path $PSScriptRoot -Filter "dforge.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { $dforge = $found.FullName }
}
if (-not $dforge) {
    Write-Warning "Couldn't find dforge.exe. Re-run with -Rebuild."
    exit 1
}
Write-Host "Using: $dforge"
& $dforge --version

Write-Host "`n-- Optical drives Windows sees --" -ForegroundColor Cyan
Get-CimInstance -ClassName Win32_CDROMDrive -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host ("  {0}  {1} {2}" -f $_.Drive, $_.Caption, $(if ($_.MediaLoaded) { "(media loaded)" } else { "(no media)" }))
}

Write-Host "`n=== Step 1: drive-profile (advertised capabilities + overread + cache-defeat) ===" -ForegroundColor Cyan
Write-Host "dforge drive-profile ${Drive}: --out drive-profile.json"
& $dforge drive-profile "${Drive}:" --out drive-profile.json
$profileExit = $LASTEXITCODE
Write-Host "(exit code $profileExit)"

Write-Host "`n=== Step 2: reread-probe (Tier-B adaptive re-read on real hardware) ===" -ForegroundColor Cyan
if ($Lba -lt 0) {
    Write-Host "No -Lba given -- reading writeinfo to pick the disc's first program sector automatically." -ForegroundColor Yellow
    & $dforge writeinfo "${Drive}:"
    # Track 1 program data almost always starts at LBA 0 for a data disc; for most discs
    # LBA 0 is a safe, always-present sector to probe even though it isn't "marginal" --
    # this proves the plumbing end-to-end. Pass -Lba explicitly to target a sector you
    # actually suspect is bad.
    $Lba = 0
    Write-Host "Using LBA $Lba (pass -Lba <n> to target a specific/suspect sector instead)." -ForegroundColor Yellow
}
Write-Host "dforge reread-probe ${Drive}: --lba $Lba"
& $dforge reread-probe "${Drive}:" --lba $Lba
$rereadExit = $LASTEXITCODE
Write-Host "(exit code $rereadExit)"

Write-Host "`n=== Summary ===" -ForegroundColor Cyan
Write-Host "drive-profile exit: $profileExit   reread-probe exit: $rereadExit"
Write-Host "Paste the full console output above back into the chat."
