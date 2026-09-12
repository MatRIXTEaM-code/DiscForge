# DiscForge -- rung 7 hardware test: mixed-mode audio track readback + verify
# Run this from C:\dev\DiscForge in a fresh PowerShell window.
#
# What it does:
#   1. Locates dforge.exe (builds first if -Rebuild is passed, or if it can't find one)
#   2. Checks the drive letter and golden reference image exist before touching the drive
#   3. Reads the audio track (track 2 by default) off the disc in D:
#   4. Verifies that readback against your golden reference image
#   5. Prints a PASS / PASS-with-notes / FAIL summary and where the HTML report landed
#
# Usage:
#   .\hw-test-rung7.ps1
#   .\hw-test-rung7.ps1 -Drive E -Track 2 -Golden C:\dev\golden\mixedmode-golden.img
#   .\hw-test-rung7.ps1 -Rebuild

param(
    [string]$Drive   = "D",
    [int]$Track      = 2,
    [string]$Golden  = "golden.img",
    [switch]$Rebuild
)

$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

Write-Host "=== DiscForge rung 7 hardware test: mixed-mode audio readback ===" -ForegroundColor Cyan
Write-Host "Working directory: $PSScriptRoot"

# --- 1. Locate (or build) dforge.exe -----------------------------------------------------------
$dforge = $null

if ($Rebuild) {
    Write-Host "`n-- Rebuild requested, running build.ps1 -Rebuild --" -ForegroundColor Yellow
    & "$PSScriptRoot\build.ps1" -Rebuild
    if ($LASTEXITCODE -ne 0) { Write-Error "build.ps1 failed with exit code $LASTEXITCODE"; exit 1 }
}

$candidates = @(
    "$PSScriptRoot\src\DiscForge.Cli\bin\Release\net8.0-windows\dforge.exe",
    "$PSScriptRoot\src\DiscForge.Cli\bin\Debug\net8.0-windows\dforge.exe",
    "C:\tools\dforge\dforge.exe"
)
foreach ($c in $candidates) {
    if (Test-Path $c) { $dforge = $c; break }
}
if (-not $dforge) {
    $found = Get-ChildItem -Path $PSScriptRoot -Filter "dforge.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { $dforge = $found.FullName }
}
if (-not $dforge) {
    Write-Warning "Couldn't find dforge.exe. Re-run with -Rebuild to build it first:"
    Write-Host "    .\hw-test-rung7.ps1 -Rebuild" -ForegroundColor Yellow
    exit 1
}
Write-Host "Using: $dforge"
& $dforge --version

# --- 2. Pre-flight checks ------------------------------------------------------------------------
Write-Host "`n-- Pre-flight checks --" -ForegroundColor Cyan

# List every optical drive Windows sees, regardless of whether media is inserted --
# Test-Path on a drive root fails for an empty tray even with the right letter, so
# that alone is not a reliable check.
$cdroms = Get-CimInstance -ClassName Win32_CDROMDrive -ErrorAction SilentlyContinue
if ($cdroms) {
    Write-Host "Optical drives detected by Windows:"
    foreach ($c in $cdroms) {
        Write-Host ("  {0}  {1} {2}" -f $c.Drive, $c.Caption, $(if ($c.MediaLoaded) { "(media loaded)" } else { "(no media / tray empty)" }))
    }
} else {
    Write-Warning "Windows doesn't report any optical drives via WMI. Continuing anyway -- dforge talks to the drive over SPTI, not the filesystem, so this isn't necessarily fatal."
}

$matched = $cdroms | Where-Object { $_.Drive -eq "${Drive}:" }
if (-not $matched) {
    Write-Warning "Drive letter ${Drive}: wasn't in the optical-drive list above. Pass -Drive <letter> matching one of the drives listed (or 'dforge drives' below)."
}

Write-Host "`n-- dforge's own drive list --" -ForegroundColor Cyan
& $dforge drives

if (-not (Test-Path $Golden)) {
    Write-Warning "Golden reference image not found at: $Golden"
    Write-Host "This needs to be the known-good reference for the mixed-mode disc's audio track" -ForegroundColor Yellow
    Write-Host "(the same golden image used for the earlier rung 1-6 PASS results)." -ForegroundColor Yellow
    Write-Host "Pass its path with -Golden <path> and re-run." -ForegroundColor Yellow
    exit 1
}
Write-Host "Golden reference found: $Golden"

Write-Host "`n-- Drive status (writeinfo) --" -ForegroundColor Cyan
& $dforge writeinfo "${Drive}:"

# --- 3. Read the audio track ----------------------------------------------------------------------
$audioOut = "audio_rb.bin"
Write-Host "`n-- Reading track $Track off ${Drive}: --" -ForegroundColor Cyan
Write-Host "dforge read-raw ${Drive}: $audioOut --track $Track"
& $dforge read-raw "${Drive}:" $audioOut --track $Track
$readExit = $LASTEXITCODE
if ($readExit -ne 0) {
    Write-Error "read-raw exited with code $readExit -- see output above."
    exit $readExit
}

if (-not (Test-Path $audioOut)) {
    Write-Error "read-raw reported success but $audioOut wasn't created."
    exit 1
}
$sizeMb = [math]::Round((Get-Item $audioOut).Length / 1MB, 2)
Write-Host "Wrote $audioOut ($sizeMb MB)."

# --- 4. Verify against the golden reference ---------------------------------------------------
$report = "cert-audio.html"
Write-Host "`n-- Verifying readback against golden reference --" -ForegroundColor Cyan
Write-Host "dforge raw-verify-readback $Golden $audioOut --partial --report $report"
& $dforge raw-verify-readback $Golden $audioOut --partial --report $report
$verifyExit = $LASTEXITCODE

Write-Host "`n=== Result ===" -ForegroundColor Cyan
if ($verifyExit -eq 0) {
    Write-Host "raw-verify-readback exited 0 (PASS or PASS-with-notes)." -ForegroundColor Green
} else {
    Write-Host "raw-verify-readback exited $verifyExit -- check the console output and $report for details." -ForegroundColor Red
}
if (Test-Path $report) {
    Write-Host "HTML report: $PSScriptRoot\$report"
}

Write-Host "`nPaste the full console output above back into the chat (and the report contents if it flagged anything)."
exit $verifyExit
