# DiscForge - final sign-off checklist.
#
# Walks the three closing tasks in order:
#   1. Launch the standalone License Generator so you can confirm it opens & issues a key.
#   2. Remind you to run the in-app activation test (manual - can't be automated).
#   3. Back up keys\private.pem to a location you name (the one file that can't be regenerated).
#
# Usage (from C:\dev\DiscForge):
#   .\finish-setup.ps1                          # prompts for a backup folder
#   .\finish-setup.ps1 -BackupTo E:\safe        # back up private.pem to E:\safe
#   .\finish-setup.ps1 -SkipGenerator           # skip step 1 (e.g. re-running just for the backup)

param(
    [string] $BackupTo,
    [switch] $SkipGenerator
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }

function Section($n, $title) {
    Write-Host ""
    Write-Host "=== Step $n : $title ===" -ForegroundColor Cyan
}

# ---------------------------------------------------------------------------
# 1. License Generator
# ---------------------------------------------------------------------------
Section 1 "Launch the standalone License Generator"
$genProj = Join-Path $root 'tools\LicenseGen\LicenseGen.csproj'
if ($SkipGenerator) {
    Write-Host "Skipped (-SkipGenerator)." -ForegroundColor DarkGray
} elseif (-not (Test-Path $genProj)) {
    Write-Host "Can't find $genProj - skipping. (Run this from C:\dev\DiscForge.)" -ForegroundColor Yellow
} else {
    Write-Host "Opening the generator window. Confirm it appears, then issue a test key" -ForegroundColor Gray
    Write-Host "(any name/edition) and copy it - you'll paste it in step 2." -ForegroundColor Gray
    Write-Host "Close the window when done to continue this script." -ForegroundColor Gray
    dotnet run --project $genProj -c Release
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Generator exited with code $LASTEXITCODE - note anything that went wrong." -ForegroundColor Yellow
    } else {
        Write-Host "Generator closed cleanly." -ForegroundColor Green
    }
}

# ---------------------------------------------------------------------------
# 2. In-app activation test (manual)
# ---------------------------------------------------------------------------
Section 2 "In-app activation test (do this by hand)"
Write-Host @"
  a. Launch the app:            .\run-app.ps1   (or run DiscForge.exe)
  b. Open the About tile, click 'Activate'.
  c. Paste the key you issued in step 1, click Activate.
  d. Confirm BOTH of these clear:
        - the title bar no longer says 'UNLICENSED (evaluation)'
        - the watermark banner disappears
  If both clear, the whole license path works end to end.
"@ -ForegroundColor Gray

# ---------------------------------------------------------------------------
# 3. Back up the private key
# ---------------------------------------------------------------------------
Section 3 "Back up keys\private.pem offline"
$key = Join-Path $root 'keys\private.pem'
if (-not (Test-Path $key)) {
    Write-Host "No keys\private.pem found yet." -ForegroundColor Yellow
    Write-Host "If you haven't created your keypair, run:  .\new-license.ps1 -Setup" -ForegroundColor Yellow
    Write-Host "then re-run:  .\finish-setup.ps1 -SkipGenerator" -ForegroundColor Yellow
} else {
    if (-not $BackupTo) {
        $BackupTo = Read-Host "Enter a backup folder for private.pem (e.g. E:\safe, or a synced vault folder)"
    }
    if ([string]::IsNullOrWhiteSpace($BackupTo)) {
        Write-Host "No backup folder given - skipping. Re-run with -BackupTo <folder> when ready." -ForegroundColor Yellow
    } else {
        if (-not (Test-Path $BackupTo)) { New-Item -ItemType Directory -Path $BackupTo -Force | Out-Null }
        $stamp = Get-Date -Format 'yyyyMMdd'
        $dest = Join-Path $BackupTo "DiscForge-private-$stamp.pem"
        Copy-Item $key $dest -Force
        Write-Host "Backed up to: $dest" -ForegroundColor Green
        Write-Host "Keep this offline and private. Anyone with this file can issue valid licenses." -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Checklist complete." -ForegroundColor Cyan
