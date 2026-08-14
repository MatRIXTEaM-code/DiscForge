# DiscForge - make a CHD sample for validation.
#
# Generates a small, compressible test CD track (so chdman actually uses the zlib
# codec, exercising the decoder), converts it to a cdzl-compressed .chd, and
# leaves the source + the .chd together in .\samples so extraction can be checked
# byte-for-byte.
#
# Usage (from C:\dev\DiscForge):
#   powershell -ExecutionPolicy Bypass -File .\make-chd-sample.ps1
#   powershell -ExecutionPolicy Bypass -File .\make-chd-sample.ps1 -Cue "path\to\game.cue"
#   powershell -ExecutionPolicy Bypass -File .\make-chd-sample.ps1 -Chdman "C:\tools\chdman.exe"
#
# chdman.exe is a standalone tool from the MAME project - no installer, no driver,
# runs fine on Windows 11. Put it on your PATH, in this folder, or pass -Chdman.

param(
    [string]$Cue     = "",
    [string]$Chdman  = "",
    [int]   $Sectors = 1200
)

$ErrorActionPreference = "Stop"

# Script's own folder (reliable under -File). Fall back to the current directory.
$root = $PSScriptRoot
if ([string]::IsNullOrEmpty($root)) { $root = (Get-Location).Path }
$samples = Join-Path $root "samples"
New-Item -ItemType Directory -Force -Path $samples | Out-Null

# --- locate chdman -----------------------------------------------------------
$chd = ""
if ($Chdman -and (Test-Path $Chdman)) {
    $chd = (Resolve-Path $Chdman).Path
} else {
    $onPath = Get-Command chdman -ErrorAction SilentlyContinue
    if ($onPath) {
        $chd = $onPath.Source
    } else {
        foreach ($p in @(
            (Join-Path $root "chdman.exe"),
            (Join-Path $root "chdman\chdman.exe"),
            (Join-Path $samples "chdman.exe"))) {
            if (Test-Path $p) { $chd = $p; break }
        }
    }
}

if ([string]::IsNullOrEmpty($chd)) {
    Write-Host "chdman.exe not found." -ForegroundColor Yellow
    Write-Host "Download it (search 'MAME chdman' - it is a standalone .exe, no install),"
    Write-Host "then put chdman.exe in this folder:"
    Write-Host "  $root"
    Write-Host "or re-run with:  -Chdman C:\path\to\chdman.exe"
    exit 1
}
Write-Host "Using chdman: $chd"

# --- pick or generate the source cue/bin -------------------------------------
if ($Cue) {
    if (-not (Test-Path $Cue)) { throw "Cue not found: $Cue" }
    $srcCue = (Resolve-Path $Cue).Path
    Write-Host "Source cue: $srcCue"
} else {
    $srcBin = Join-Path $samples "chd-src.bin"
    $srcCue = Join-Path $samples "chd-src.cue"
    Write-Host ("Generating a {0}-sector test track -> {1}" -f $Sectors, $srcBin)

    # One structured (compressible) 2352-byte sector, varied slightly per sector.
    $sector = New-Object byte[] 2352
    for ($i = 0; $i -lt 2352; $i++) { $sector[$i] = [byte]($i % 251) }

    $fs = [System.IO.File]::Create($srcBin)
    try {
        for ($s = 0; $s -lt $Sectors; $s++) {
            $sector[0] = [byte]($s -band 0xFF)
            $sector[1] = [byte](($s -shr 8) -band 0xFF)
            $fs.Write($sector, 0, 2352)
        }
    } finally { $fs.Dispose() }

    $cueText = "FILE `"chd-src.bin`" BINARY`r`n  TRACK 01 AUDIO`r`n    INDEX 01 00:00:00`r`n"
    [System.IO.File]::WriteAllText($srcCue, $cueText, [System.Text.Encoding]::ASCII)
    Write-Host "Wrote $srcCue"
}

# --- make the CHDs (one per codec, so each decoder can be validated) ---------
# Forcing a codec makes chdman use it for every hunk, so -c cdlz yields an
# all-LZMA image and -c cdfl an all-FLAC one - exactly what's needed to test
# those decoders. The default run leaves codec selection to chdman (the mix a
# real image uses).
$variants = @(
    @{ name = "test-cdzl.chd";    args = @("-c", "cdzl") },
    @{ name = "test-cdlz.chd";    args = @("-c", "cdlz") },
    @{ name = "test-cdfl.chd";    args = @("-c", "cdfl") },
    @{ name = "test-default.chd"; args = @() }
)
foreach ($v in $variants) {
    $outChd = Join-Path $samples $v.name
    Write-Host "Creating $($v.name)..."
    & $chd createcd -i $srcCue -o $outChd @($v.args) -f
    if ($LASTEXITCODE -ne 0) { throw "chdman failed for $($v.name) (exit $LASTEXITCODE)." }
}

Write-Host ""
Write-Host "Done. Files for validation are in:" -ForegroundColor Green
Write-Host "  $samples"
Get-ChildItem $samples | Select-Object Name, Length | Format-Table -AutoSize
Write-Host "Now tell Claude: the CHD sample is ready in the samples folder."
