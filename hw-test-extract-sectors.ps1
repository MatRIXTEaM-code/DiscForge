# DiscForge -- validate extract-sectors' drive-mode against the already-proven mixed-mode disc.
# Ground truth: data_rb.bin (450 raw sectors, track 1) and audio_rb.bin (350 raw sectors, track 2)
# from the rung-7 hardware test -- both already confirmed byte-identical to the correct golden.
#
# Cases: single sector, whole track x2, a cross-track span (data->audio boundary), the last
# sector, and a clean out-of-range rejection.
#
# Usage: .\hw-test-extract-sectors.ps1 -Drive D

param([string]$Drive = "D")

$ErrorActionPreference = "Continue"
Set-Location -Path $PSScriptRoot

function Get-Dforge {
    $candidates = @(
        "$PSScriptRoot\src\DiscForge.Cli\bin\Release\net8.0-windows\dforge.exe",
        "$PSScriptRoot\src\DiscForge.Cli\bin\Debug\net8.0-windows\dforge.exe",
        "C:\tools\dforge\dforge.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    $found = Get-ChildItem -Path $PSScriptRoot -Filter "dforge.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { return $found.FullName }
    return $null
}

function Bytes-Equal($a, $b) {
    if ($a.Length -ne $b.Length) { return $false }
    for ($i = 0; $i -lt $a.Length; $i++) { if ($a[$i] -ne $b[$i]) { return $false } }
    return $true
}

$dforge = Get-Dforge
if (-not $dforge) { Write-Error "dforge.exe not found. Run build.ps1 -Rebuild first."; exit 1 }
Write-Host "Using: $dforge"
& $dforge --version

if (-not (Test-Path "data_rb.bin") -or -not (Test-Path "audio_rb.bin")) {
    Write-Error "data_rb.bin / audio_rb.bin not found in this folder -- these are the rung-7 ground truth. Re-run the rung-7 read-raw steps first if they were cleaned up."
    exit 1
}

$dataRb = [System.IO.File]::ReadAllBytes("data_rb.bin")
$audioRb = [System.IO.File]::ReadAllBytes("audio_rb.bin")
Write-Host "Ground truth: data_rb.bin $($dataRb.Length) bytes (450 sectors), audio_rb.bin $($audioRb.Length) bytes (350 sectors)"

$results = @()

Write-Host "`n=== Case 1: single sector (LBA 5, track 1) ===" -ForegroundColor Cyan
& $dforge extract-sectors "${Drive}:" es-single.bin --start 5 --end 5
if (Test-Path es-single.bin) {
    $got = [System.IO.File]::ReadAllBytes("es-single.bin")
    $want = $dataRb[(5*2352)..(6*2352-1)]
    $ok = Bytes-Equal $got $want
    Write-Host $(if ($ok) { "PASS" } else { "FAIL - byte mismatch" }) -ForegroundColor $(if ($ok) { "Green" } else { "Red" })
    $results += [pscustomobject]@{Case="single sector"; Pass=$ok}
} else { Write-Host "FAIL - no output file" -ForegroundColor Red; $results += [pscustomobject]@{Case="single sector"; Pass=$false} }

Write-Host "`n=== Case 2: whole track 1 (data) ===" -ForegroundColor Cyan
& $dforge extract-sectors "${Drive}:" es-track1.bin --track 1
if (Test-Path es-track1.bin) {
    $got = [System.IO.File]::ReadAllBytes("es-track1.bin")
    $ok = Bytes-Equal $got $dataRb
    Write-Host $(if ($ok) { "PASS" } else { "FAIL - byte mismatch ($($got.Length) vs $($dataRb.Length) bytes)" }) -ForegroundColor $(if ($ok) { "Green" } else { "Red" })
    $results += [pscustomobject]@{Case="track 1 (data)"; Pass=$ok}
} else { Write-Host "FAIL - no output file" -ForegroundColor Red; $results += [pscustomobject]@{Case="track 1 (data)"; Pass=$false} }

Write-Host "`n=== Case 3: whole track 2 (audio) ===" -ForegroundColor Cyan
& $dforge extract-sectors "${Drive}:" es-track2.bin --track 2
if (Test-Path es-track2.bin) {
    $got = [System.IO.File]::ReadAllBytes("es-track2.bin")
    $ok = Bytes-Equal $got $audioRb
    Write-Host $(if ($ok) { "PASS" } else { "FAIL - byte mismatch ($($got.Length) vs $($audioRb.Length) bytes)" }) -ForegroundColor $(if ($ok) { "Green" } else { "Red" })
    $results += [pscustomobject]@{Case="track 2 (audio)"; Pass=$ok}
} else { Write-Host "FAIL - no output file" -ForegroundColor Red; $results += [pscustomobject]@{Case="track 2 (audio)"; Pass=$false} }

Write-Host "`n=== Case 4: cross-track span (LBA 445-455, data->audio boundary) ===" -ForegroundColor Cyan
& $dforge extract-sectors "${Drive}:" es-cross.bin --start 445 --end 455
if (Test-Path es-cross.bin) {
    $got = [System.IO.File]::ReadAllBytes("es-cross.bin")
    $wantData = $dataRb[(445*2352)..(450*2352-1)]        # LBA 445-449, last 5 sectors of track 1
    $wantAudio = $audioRb[0..(6*2352-1)]                  # LBA 450-455, first 6 sectors of track 2
    $want = $wantData + $wantAudio
    $ok = Bytes-Equal $got $want
    Write-Host $(if ($ok) { "PASS" } else { "FAIL - byte mismatch ($($got.Length) vs $($want.Length) bytes)" }) -ForegroundColor $(if ($ok) { "Green" } else { "Red" })
    $results += [pscustomobject]@{Case="cross-track span"; Pass=$ok}
} else { Write-Host "FAIL - no output file" -ForegroundColor Red; $results += [pscustomobject]@{Case="cross-track span"; Pass=$false} }

Write-Host "`n=== Case 5: last sector (LBA 799) ===" -ForegroundColor Cyan
& $dforge extract-sectors "${Drive}:" es-last.bin --start 799 --end 799
if (Test-Path es-last.bin) {
    $got = [System.IO.File]::ReadAllBytes("es-last.bin")
    $want = $audioRb[(349*2352)..(350*2352-1)]   # last sector of the 350-sector audio track
    $ok = Bytes-Equal $got $want
    Write-Host $(if ($ok) { "PASS" } else { "FAIL - byte mismatch" }) -ForegroundColor $(if ($ok) { "Green" } else { "Red" })
    $results += [pscustomobject]@{Case="last sector"; Pass=$ok}
} else { Write-Host "FAIL - no output file" -ForegroundColor Red; $results += [pscustomobject]@{Case="last sector"; Pass=$false} }

Write-Host "`n=== Case 6: out-of-range request (LBA 800, past the disc) -- must fail cleanly ===" -ForegroundColor Cyan
Remove-Item es-oob.bin -ErrorAction SilentlyContinue
& $dforge extract-sectors "${Drive}:" es-oob.bin --start 800 --end 800
$oobExit = $LASTEXITCODE
$oobOk = ($oobExit -ne 0) -and (-not (Test-Path es-oob.bin) -or (Get-Item es-oob.bin).Length -eq 0)
Write-Host $(if ($oobOk) { "PASS - rejected cleanly (exit $oobExit)" } else { "FAIL - did not reject cleanly (exit $oobExit)" }) -ForegroundColor $(if ($oobOk) { "Green" } else { "Red" })
$results += [pscustomobject]@{Case="out-of-range rejection"; Pass=$oobOk}

Write-Host "`n=== Summary ===" -ForegroundColor Cyan
$results | Format-Table -AutoSize
$allPass = ($results | Where-Object { -not $_.Pass }).Count -eq 0
Write-Host $(if ($allPass) { "ALL CASES PASS" } else { "SOME CASES FAILED -- see above" }) -ForegroundColor $(if ($allPass) { "Green" } else { "Red" })
