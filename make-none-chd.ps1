# DiscForge - build a CHD containing uncompressed (NONE) hunks.
#
# chdman stores a hunk raw ("NONE") when no codec beats storing it as-is. Random
# data is incompressible, so a track of random bytes yields NONE hunks - exactly
# what's needed to validate the extractor's NONE fallback.
#
# Usage (from C:\dev\DiscForge):
#   powershell -ExecutionPolicy Bypass -File .\make-none-chd.ps1

param([string]$Chdman = "", [int]$Sectors = 300)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if ([string]::IsNullOrEmpty($root)) { $root = (Get-Location).Path }
$samples = Join-Path $root "samples"
New-Item -ItemType Directory -Force -Path $samples | Out-Null

# locate chdman
$chd = ""
if ($Chdman -and (Test-Path $Chdman)) { $chd = (Resolve-Path $Chdman).Path }
else {
    $onPath = Get-Command chdman -ErrorAction SilentlyContinue
    if ($onPath) { $chd = $onPath.Source }
    else { foreach ($p in @((Join-Path $root "chdman.exe"), (Join-Path $root "chdman\chdman.exe"), (Join-Path $samples "chdman.exe"))) { if (Test-Path $p) { $chd = $p; break } } }
}
if ([string]::IsNullOrEmpty($chd)) { Write-Host "chdman.exe not found - put it in $root or pass -Chdman." -ForegroundColor Yellow; exit 1 }
$PSNativeCommandUseErrorActionPreference = $false
Write-Host "Using chdman: $chd"

# random (incompressible) audio track
$srcBin = Join-Path $samples "none-src.bin"
$srcCue = Join-Path $samples "none-src.cue"
Write-Host "Generating $Sectors sectors of random data -> $srcBin"
$bytes = New-Object byte[] ($Sectors * 2352)
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
[System.IO.File]::WriteAllBytes($srcBin, $bytes)
$cueText = "FILE `"none-src.bin`" BINARY`r`n  TRACK 01 AUDIO`r`n    INDEX 01 00:00:00`r`n"
[System.IO.File]::WriteAllText($srcCue, $cueText, [System.Text.Encoding]::ASCII)

# chdman (default codecs; random data won't compress, so hunks land as NONE)
$outChd = Join-Path $samples "test-none.chd"
Write-Host "Compressing to $outChd ..."
& $chd createcd -i $srcCue -o $outChd -f
if ($LASTEXITCODE -ne 0) { throw "chdman failed (exit $LASTEXITCODE)." }

Write-Host ""
Write-Host "Done. If the reported ratio is ~100%, the hunks are stored NONE." -ForegroundColor Green
Get-ChildItem $samples -Filter "none*" | Select-Object Name, Length | Format-Table -AutoSize
Write-Host "Now tell Claude: the NONE CHD is ready."
