# DiscForge - build a small DATA-track CHD to validate CHD data-sector extraction.
#
# Real game CHDs have data tracks (Mode 2/2352) whose ECC chdman strips and
# regenerates. A full game CHD is hundreds of MB; this instead uses DiscForge's
# own psx-build to lay down a handful of valid Mode 2/2352 sectors (real EDC/ECC),
# then chdman-compresses them, giving a tiny CHD that still exercises the ECC path.
#
# Usage (from C:\dev\DiscForge):
#   powershell -ExecutionPolicy Bypass -File .\make-data-chd.ps1
#
# Needs chdman (as before) and builds dforge (the CLI) if it isn't built yet.

param([string]$Chdman = "")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if ([string]::IsNullOrEmpty($root)) { $root = (Get-Location).Path }
$samples = Join-Path $root "samples"
New-Item -ItemType Directory -Force -Path $samples | Out-Null

# --- locate chdman -----------------------------------------------------------
$chd = ""
if ($Chdman -and (Test-Path $Chdman)) { $chd = (Resolve-Path $Chdman).Path }
else {
    $onPath = Get-Command chdman -ErrorAction SilentlyContinue
    if ($onPath) { $chd = $onPath.Source }
    else { foreach ($p in @((Join-Path $root "chdman.exe"), (Join-Path $root "chdman\chdman.exe"), (Join-Path $samples "chdman.exe"))) { if (Test-Path $p) { $chd = $p; break } } }
}
if ([string]::IsNullOrEmpty($chd)) { Write-Host "chdman.exe not found - put it in $root or pass -Chdman." -ForegroundColor Yellow; exit 1 }
Write-Host "Using chdman: $chd"

# --- build dforge (the CLI) fresh -------------------------------------------
# Always rebuild: an old dforge.dll from before `psx-build` existed would fail
# with "unknown command", so we don't trust whatever is already in bin.
function Find-Dforge { Get-ChildItem -Path (Join-Path $root "src\DiscForge.Cli\bin\Release") -Filter dforge.dll -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1 }
Write-Host "Building dforge (the CLI) fresh..."
dotnet build (Join-Path $root "src\DiscForge.Cli\DiscForge.Cli.csproj") -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "dforge build failed - see the errors above." }
$dforge = Find-Dforge
if (-not $dforge) { throw "Built, but dforge.dll wasn't found under src\DiscForge.Cli\bin\Release." }
Write-Host "Using dforge: $($dforge.FullName)"

# Native tools (dforge, chdman) write status to stderr; don't let PowerShell treat
# that as a terminating error - we check exit codes explicitly instead.
$PSNativeCommandUseErrorActionPreference = $false

# --- make a small source folder ---------------------------------------------
$srcDir = Join-Path $samples "data-src"
if (Test-Path $srcDir) { Remove-Item $srcDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $srcDir | Out-Null
foreach ($i in 1..4) {
    $body = ("DiscForge data-track test file $i. " * 400)
    Set-Content -Path (Join-Path $srcDir ("FILE{0}.DAT" -f $i)) -Value $body -Encoding Ascii
}

# --- psx-build -> valid Mode 2/2352 bin/cue ---------------------------------
$dataBin = Join-Path $samples "data.bin"
$dataCue = Join-Path $samples "data.cue"
Write-Host "Building a Mode 2/2352 data image with psx-build..."
dotnet $dforge.FullName psx-build $srcDir $dataBin "PSXDATA" $dataCue
if ($LASTEXITCODE -ne 0) { throw "psx-build failed (exit $LASTEXITCODE)." }

# --- chdman -> data-track CHD -----------------------------------------------
$outChd = Join-Path $samples "test-data.chd"
Write-Host "Compressing to $outChd (default codecs)..."
& $chd createcd -i $dataCue -o $outChd -f
if ($LASTEXITCODE -ne 0) { throw "chdman failed (exit $LASTEXITCODE)." }

Write-Host ""
Write-Host "Done. Data-track CHD + source are in $samples :" -ForegroundColor Green
Get-ChildItem $samples -Filter "data*" | Select-Object Name, Length | Format-Table -AutoSize
Get-ChildItem $samples -Filter "test-data.chd" | Select-Object Name, Length | Format-Table -AutoSize
Write-Host "Now tell Claude: the data CHD is ready."
