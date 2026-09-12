# DiscForge -- GameCube disc detection check on the SH-224 (or whichever drive letter you pass).
# Run this with the GameCube disc loaded. It doesn't attempt a read -- it just reports what
# Windows/DiscForge see at the drive and SCSI/MMC level, so we can tell whether the disc is
# actually spinning up and being recognized before trying a real dump.
#
# Usage:
#   .\gc-drive-check.ps1
#   .\gc-drive-check.ps1 -Drive E -Rebuild

param(
    [string]$Drive = "D",
    [switch]$Rebuild
)

$ErrorActionPreference = "Continue"
Set-Location -Path $PSScriptRoot

Write-Host "=== DiscForge GameCube disc detection check ===" -ForegroundColor Cyan

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

Write-Host "`n-- Optical drives Windows sees (WMI) --" -ForegroundColor Cyan
Get-CimInstance -ClassName Win32_CDROMDrive -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host ("  {0}  {1} {2}" -f $_.Drive, $_.Caption, $(if ($_.MediaLoaded) { "(media loaded)" } else { "(no media / tray empty)" }))
}

Write-Host "`n-- dforge's own drive list --" -ForegroundColor Cyan
& $dforge drives

Write-Host "`n-- writeinfo (disc status at the SCSI/MMC level) --" -ForegroundColor Cyan
& $dforge writeinfo "${Drive}:"
$writeinfoExit = $LASTEXITCODE

Write-Host "`n-- drive-profile (capabilities + overread/cache-defeat probes) --" -ForegroundColor Cyan
& $dforge drive-profile "${Drive}:" --out sh224-gc-profile.json
$profileExit = $LASTEXITCODE

Write-Host "`n=== Summary ===" -ForegroundColor Cyan
Write-Host "writeinfo exit: $writeinfoExit   drive-profile exit: $profileExit"
Write-Host ""
Write-Host "If writeinfo/drive-profile above show real disc/media info (not blank, not an error, not"
Write-Host "'no media'), the drive IS seeing the GameCube disc at the hardware level -- Explorer not"
Write-Host "showing files is normal and expected (GC uses a proprietary filesystem Windows can't read)."
Write-Host "If they instead report no media / a SCSI error / a timeout, that's the real signal --"
Write-Host "paste the full output back and we'll dig into whether it's the disc or the drive."
