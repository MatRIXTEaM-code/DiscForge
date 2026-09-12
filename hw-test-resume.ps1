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

# This fixture exists for ONE purpose: give you a rip that takes long enough (a minute or more)
# to reliably Ctrl+C partway through, so --resume's "skip the tracks that already finished" logic
# actually gets exercised. The rung-7 mixed.cue fixture rips in well under a second - too fast to
# interrupt by hand - so this uses a much bigger (but still silent/zero-filled, so it burns fast
# and needs no real audio source) second track instead.
#
# Sizing: track 1 (data) stays small so it finishes almost immediately - that's the "at least one
# track already completed" case --resume is meant to skip. Track 2 (audio) is ~250,000 sectors
# (~561 MB, ~55 minutes of silence) so it takes a real drive on the order of a minute or more to
# read, giving you a comfortable window to interrupt. Total disc usage (~562 MB) fits well within
# a standard 700 MB / 80-minute CD-R.

Set-Location $PSScriptRoot
# Set-Location only moves PowerShell's OWN location provider ($PWD) - it does not reliably sync
# .NET's process-wide CurrentDirectory in every host/elevation context (a real repro: an elevated
# PowerShell shortcut that starts in C:\Windows\system32 left [IO.File]::WriteAllBytes("data.bin", ...)
# resolving against system32 and failing with UnauthorizedAccessException, even though `Write-Host
# "Working directory: $PSScriptRoot"` printed the CORRECT path immediately above it - Set-Location
# had visibly worked for PowerShell's own view, just not for plain .NET file APIs). Every .NET call
# below (WriteAllBytes, File.Create) uses a relative path and resolves it against
# [Environment]::CurrentDirectory, so that has to be set explicitly too.
[Environment]::CurrentDirectory = $PSScriptRoot
Write-Host "Working directory: $PSScriptRoot"

Section "Drive check"
Get-CimInstance -ClassName Win32_CDROMDrive | Select-Object Name, Drive, MediaLoaded | Format-Table -AutoSize
& $dforge drives

Section "Free space check (need at least ~1.2 GB free for the fixture + golden image)"
Get-PSDrive -Name ($PSScriptRoot.Substring(0,1)) | Select-Object Name, @{N='FreeGB';E={[Math]::Round($_.Free/1GB,2)}}

Section "Building the oversized resume-test fixture (data.bin small, a-big.bin ~561 MB of silence)"
$d = New-Object byte[] (2048*300)
for ($i = 0; $i -lt $d.Length; $i++) { $d[$i] = ($i * 7) -band 0xff }
[IO.File]::WriteAllBytes("data.bin", $d)
Get-Item data.bin | Select-Object Name, Length

$targetBytes = 2352L * 250000L

if ((Test-Path a-big.bin) -and (Get-Item a-big.bin).Length -eq $targetBytes) {
    Write-Host "a-big.bin already exists at the expected size ($targetBytes bytes) - reusing it, not rewriting."
} else {
    # Written in 4 MB chunks rather than via FileStream.SetLength - SetLength can fail quietly (disk
    # space, or security software flagging a newly-created large all-zero file) without PowerShell
    # surfacing a clear error. That's exactly what happened the first time this script ran: no
    # exception, but the file never persisted - almost certainly antivirus/Controlled Folder Access
    # silently reverting a large all-zero write as a ransomware-style heuristic. If this still fails,
    # the file has already been generated once and dropped directly into this folder from outside
    # PowerShell, so this branch should only run if that file is ever missing or the wrong size.
    Write-Host "Writing a-big.bin ($targetBytes bytes) in 4 MB chunks - this can take a little while..."
    Remove-Item -ErrorAction SilentlyContinue a-big.bin
    try {
        $bufSize = 4MB
        $buf = New-Object byte[] $bufSize
        $fs = [IO.File]::Create("a-big.bin")
        try {
            $written = [long]0
            while ($written -lt $targetBytes) {
                $chunk = [Math]::Min([long]$bufSize, $targetBytes - $written)
                $fs.Write($buf, 0, $chunk)
                $written += $chunk
            }
            $fs.Flush()
        } finally {
            $fs.Close()
        }
    } catch {
        Write-Host "Failed to write a-big.bin: $($_.Exception.Message)" -ForegroundColor Red
        throw
    }
}

if (-not (Test-Path a-big.bin)) {
    throw "a-big.bin still doesn't exist. Check available disk space (see the free-space check above) and check whether antivirus/security software (e.g. Windows Defender Controlled Folder Access) is quarantining or reverting a newly created large all-zero file in this folder, then re-run this script."
}
$actualSize = (Get-Item a-big.bin).Length
if ($actualSize -ne $targetBytes) {
    throw "a-big.bin exists but is $actualSize bytes, expected $targetBytes - it was likely truncated or modified after creation. Re-run this script."
}
Get-Item a-big.bin | Select-Object Name, Length

$lines = @(
    'FILE "data.bin" BINARY',
    '  TRACK 01 MODE1/2048',
    '    INDEX 01 00:00:00',
    'FILE "a-big.bin" BINARY',
    '  TRACK 02 AUDIO',
    '    INDEX 00 00:00:00',
    '    INDEX 01 00:02:00'
)
$lines | Set-Content -Encoding ASCII resume-fixture.cue
Get-Item resume-fixture.cue | Select-Object Name, Length
Get-Content resume-fixture.cue

Section "Building resume-golden.img (this step alone may take a little while - 561 MB)"
& $dforge build-raw resume-fixture.cue resume-golden.img --subcode raw
Get-Item resume-golden.img | Select-Object Name, Length

Section "Disc status before burn"
& $dforge writeinfo $Drive
Write-Host ""
Write-Host "Make sure a BLANK disc is in the drive before continuing - check 'Next writable address' is valid" -ForegroundColor Yellow
Write-Host "and 'Free blocks' is nonzero above. If it shows the disc as finalized/not-blank, swap in a blank one" -ForegroundColor Yellow
Write-Host "and re-run this script." -ForegroundColor Yellow

Section "Burning resume-fixture.cue to $Drive (RAW DAO-96) - this will take longer than the small fixture, be patient"
& $dforge burn-raw resume-fixture.cue $Drive --engine spti
if ($LASTEXITCODE -ne 0) { throw "burn-raw failed - see output above." }

Section "Ready for the manual interrupt-and-resume test"
Write-Host ""
Write-Host "Now run this yourself, interactively (not scripted - the interrupt timing needs your judgment):" -ForegroundColor Yellow
Write-Host ""
Write-Host "  1) dforge read-cdi $Drive bigtest.cdi --resume" -ForegroundColor White
Write-Host "     Wait for the line 'track 1: 450/450 sectors' to print - that means track 1 (the fast" -ForegroundColor White
Write-Host "     data track) is done and it has moved on to track 2 (the big silent audio track)." -ForegroundColor White
Write-Host "     Then wait AT LEAST 15-20 seconds into track 2 (no output will print mid-track - that's" -ForegroundColor White
Write-Host "     expected, progress is only reported when a whole track finishes) before pressing Ctrl+C." -ForegroundColor White
Write-Host ""
Write-Host "  2) After you Ctrl+C, check that these exist:" -ForegroundColor White
Write-Host "       dir bigtest.cdi.ripstate.json" -ForegroundColor White
Write-Host "       dir bigtest.cdi.track*.tmp" -ForegroundColor White
Write-Host "     You should see the checkpoint sidecar and a completed track1 temp file (track2's temp" -ForegroundColor White
Write-Host "     file, if present, will be incomplete/undersized - that's fine, it should get discarded)." -ForegroundColor White
Write-Host ""
Write-Host "  3) dforge read-cdi $Drive bigtest.cdi --resume" -ForegroundColor White
Write-Host "     Run it again with the SAME output filename. Watch for it to report reusing track 1's" -ForegroundColor White
Write-Host "     already-captured data instead of re-reading all 450 sectors from scratch, then finish" -ForegroundColor White
Write-Host "     re-reading track 2 to completion." -ForegroundColor White
Write-Host ""
Write-Host "  4) Paste back the full console output from all three commands (interrupted run, the two" -ForegroundColor White
Write-Host "     dir checks, and the resumed run)." -ForegroundColor White
