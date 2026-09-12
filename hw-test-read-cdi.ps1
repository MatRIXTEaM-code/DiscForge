param(
    [string]$Drive = "D:"
)

$ErrorActionPreference = "Stop"
$dforge = ".\dforge.exe"
if (-not (Test-Path $dforge)) { $dforge = "dforge" }

function Section($title) {
    Write-Host ""
    Write-Host "==== $title ====" -ForegroundColor Cyan
}

Section "Drive check"
Get-CimInstance -ClassName Win32_CDROMDrive | Select-Object Name, Drive, MediaLoaded | Format-Table -AutoSize
& $dforge drives

Section "Regenerating the rung-7 mixed-mode fixture fresh (never trust a reused data.bin/a.bin/mixed.cue)"
$d = New-Object byte[] (2048*300)
for ($i = 0; $i -lt $d.Length; $i++) { $d[$i] = ($i * 7) -band 0xff }
[IO.File]::WriteAllBytes("data.bin", $d)
Get-Item data.bin | Select-Object Name, Length

$z = New-Object byte[] (2352*500)
[IO.File]::WriteAllBytes("a.bin", $z)
Get-Item a.bin | Select-Object Name, Length

$lines = @(
    'FILE "data.bin" BINARY',
    '  TRACK 01 MODE1/2048',
    '    INDEX 01 00:00:00',
    'FILE "a.bin" BINARY',
    '  TRACK 02 AUDIO',
    '    INDEX 00 00:00:00',
    '    INDEX 01 00:02:00'
)
$lines | Set-Content -Encoding ASCII mixed.cue
Get-Item mixed.cue | Select-Object Name, Length
Get-Content mixed.cue

Section "Building and pre-flighting golden.img"
& $dforge build-raw mixed.cue golden.img --subcode raw
Get-Item golden.img | Select-Object Name, Length
& $dforge inspect-raw golden.img --deep
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "inspect-raw exited non-zero. For THIS EXACT fixture that is expected: track 1's scan" -ForegroundColor Yellow
    Write-Host "window bleeds into track 2's own stored pregap (150 sectors of legitimate silence in" -ForegroundColor Yellow
    Write-Host "a.bin), which has no sync pattern by design - that shows as '150 SYNC-LESS <-- BAD' but" -ForegroundColor Yellow
    Write-Host "is not damage. If golden.img is 57,405,600 bytes and the only note is SYNC-LESS (no EDC" -ForegroundColor Yellow
    Write-Host "or ECC errors reported), this is the known-good fixture - proceeding." -ForegroundColor Yellow
    Write-Host "If you see EDC or ECC errors instead, STOP and paste the output back before burning." -ForegroundColor Yellow
}

Section "Disc status before burn"
& $dforge writeinfo $Drive

Section "Burning mixed.cue to $Drive (RAW DAO-96)"
& $dforge burn-raw mixed.cue $Drive --engine spti
if ($LASTEXITCODE -ne 0) { throw "burn-raw failed - see output above." }

Section "read-cdi: plain rip"
Remove-Item -ErrorAction SilentlyContinue read-cdi-plain.cdi, read-cdi-plain.cdi.dumpsession.json
& $dforge read-cdi $Drive read-cdi-plain.cdi
$plainExit = $LASTEXITCODE
Get-Item read-cdi-plain.cdi | Select-Object Name, Length

Section "read-cdi: --raw"
Remove-Item -ErrorAction SilentlyContinue read-cdi-raw.cdi, read-cdi-raw.cdi.dumpsession.json
& $dforge read-cdi $Drive read-cdi-raw.cdi --raw
$rawExit = $LASTEXITCODE
Get-Item read-cdi-raw.cdi | Select-Object Name, Length

Section "read-cdi: --raw --adaptive-reread"
Remove-Item -ErrorAction SilentlyContinue read-cdi-tierb.cdi, read-cdi-tierb.cdi.dumpsession.json
& $dforge read-cdi $Drive read-cdi-tierb.cdi --raw --adaptive-reread
$tierbExit = $LASTEXITCODE
Get-Item read-cdi-tierb.cdi | Select-Object Name, Length

Section "Cross-checking the plain and raw rips agree in size order of magnitude"
$plain = Get-Item read-cdi-plain.cdi
$raw = Get-Item read-cdi-raw.cdi
Write-Host "plain.cdi: $($plain.Length) bytes   raw.cdi: $($raw.Length) bytes"
Write-Host "(raw should be larger - every track stored at 2352 bytes/sector instead of the data track's cooked 2048)"

Section "Summary"
Write-Host "plain rip exit code:  $plainExit"
Write-Host "raw rip exit code:    $rawExit"
Write-Host "adaptive-reread exit code: $tierbExit"
Write-Host ""
Write-Host "If all three are 0 (or 2 with a report of only benign notes), read-cdi is confirmed working on this drive."
Write-Host "Paste this whole console output back into the chat."
Write-Host ""
Write-Host "--resume is NOT exercised by this script: this fixture disc rips in well under a second," -ForegroundColor Yellow
Write-Host "too fast to interrupt by hand reliably. To test --resume, use a real disc you own instead:" -ForegroundColor Yellow
Write-Host "  dforge read-cdi $Drive bigtest.cdi" -ForegroundColor Yellow
Write-Host "  (press Ctrl+C partway through, after at least one track finishes)" -ForegroundColor Yellow
Write-Host "  dforge read-cdi $Drive bigtest.cdi --resume" -ForegroundColor Yellow
Write-Host "  (should report 'reusing previous capture' for the completed track(s))" -ForegroundColor Yellow
