param(
    [string]$Drive = "D:",
    [int]$BufferSeconds = 20,
    [int]$Track1TimeoutSeconds = 180,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
# See hw-test-resume.ps1 for why this line exists: Set-Location alone doesn't reliably sync .NET's
# process-wide CurrentDirectory in every host/elevation context, and this script's own relative
# paths (bigtest.cdi*, the log files) are opened via .NET underneath dforge.exe and Get/Remove-Item.
[Environment]::CurrentDirectory = $PSScriptRoot

# Resolve the freshest build first. A bare "dforge" on PATH (eg. C:\tools\dforge\dforge.exe)
# can silently be a stale copy from before a command was added - that's exactly what happened
# here: PATH's dforge.exe predated read-cdi entirely, printed "Unknown or not-yet-implemented
# command 'read-cdi'", exited 0, and the script mistook that instant exit for "disc read fast."
# Always prefer the just-built CLI next to this script over anything found via PATH.
$dforge = Join-Path $PSScriptRoot "src\DiscForge.Cli\bin\$Configuration\net8.0-windows\dforge.exe"
if (-not (Test-Path $dforge)) { $dforge = Join-Path $PSScriptRoot "publish\dforge.exe" }
if (-not (Test-Path $dforge)) { $dforge = ".\dforge.exe" }
if (-not (Test-Path $dforge)) { $dforge = "dforge" }
Write-Host "Using dforge: $dforge" -ForegroundColor DarkGray

# This automates the one part of the --resume test that used to need a human watching the
# console and timing a Ctrl+C: DiscReader's checkpoint (bigtest.cdi.ripstate.json) is written
# SYNCHRONOUSLY right after each track finishes - not on a Ctrl+C/shutdown handler - and the CLI
# installs no Console.CancelKeyPress handler at all, so a hard process kill is functionally
# identical to a real Ctrl+C here: whatever track already finished is safely checkpointed, and
# the track that was mid-flight is left as an incomplete/wrong-sized .tmp file that --resume's
# own size check will correctly refuse to reuse. No blank disc needed - this only READS the
# fixture disc already burned by hw-test-resume.ps1; it does not burn anything.

function Section($title) {
    Write-Host ""
    Write-Host "==== $title ====" -ForegroundColor Cyan
}

Section "Drive check"
$media = Get-CimInstance -ClassName Win32_CDROMDrive | Where-Object { $_.Drive -eq $Drive }
if (-not $media -or -not $media.MediaLoaded) {
    throw "No media detected in $Drive. This test rips the fixture disc hw-test-resume.ps1 already burned - load it and try again (no need to re-burn)."
}
Write-Host "Media loaded in $Drive - good, no burn needed for this test."

Section "Cleaning any leftover bigtest.cdi artifacts from earlier attempts"
Remove-Item -ErrorAction SilentlyContinue bigtest.cdi, bigtest.cdi.ripstate.json, bigtest.cdi.dumpsession.json, bigtest.cdi.track*.tmp
Get-ChildItem bigtest.cdi* -ErrorAction SilentlyContinue | Format-Table Name, Length -AutoSize

Section "Pass 1: starting the rip, will interrupt it partway through track 2"
$log1 = "bigtest.cdi.pass1.log"
Remove-Item -ErrorAction SilentlyContinue $log1
$proc = Start-Process -FilePath $dforge -ArgumentList @("read-cdi", $Drive, "bigtest.cdi", "--resume") `
    -RedirectStandardOutput $log1 -NoNewWindow -PassThru
Write-Host "Started PID $($proc.Id), logging to $log1 - waiting for track 1 to finish (up to $Track1TimeoutSeconds s)..."

$track1Done = $false
$deadline = (Get-Date).AddSeconds($Track1TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    if ($proc.HasExited) { break }
    if (Test-Path $log1) {
        $hit = Select-String -Path $log1 -Pattern 'track 1:\s*(\d+)/(\d+) sectors' | Select-Object -Last 1
        if ($hit -and $hit.Matches[0].Groups[1].Value -eq $hit.Matches[0].Groups[2].Value) {
            $track1Done = $true
            Write-Host "Track 1 finished: $($hit.Line.Trim())"
            break
        }
    }
    Start-Sleep -Milliseconds 500
}

if ($proc.HasExited) {
    if (Select-String -Path $log1 -Pattern 'Unknown or not-yet-implemented command' -Quiet) {
        Get-Content $log1
        throw "The dforge at '$dforge' doesn't know the 'read-cdi' command - it's a stale/older build, not a fast disc. Rebuild first (.\build-app.ps1) or point this script at the right dforge.exe."
    }
    Write-Host ""
    Write-Host "The rip finished on its own before we could interrupt it (exit code $($proc.ExitCode))." -ForegroundColor Yellow
    Write-Host "That means this disc reads faster than expected for this fixture size - it confirms a" -ForegroundColor Yellow
    Write-Host "clean full rip, but does NOT exercise the interrupt->resume path. Re-run hw-test-resume.ps1" -ForegroundColor Yellow
    Write-Host "with a larger audio track (bump the 250000-sector count) if you want to retry this properly." -ForegroundColor Yellow
    Get-Content $log1
    exit 0
}
if (-not $track1Done) {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    throw "Track 1 never reported complete within $Track1TimeoutSeconds s - check $log1 for what actually happened."
}

Write-Host "Waiting $BufferSeconds more second(s) into track 2 before interrupting..."
Start-Sleep -Seconds $BufferSeconds

if ($proc.HasExited) {
    Write-Host "The rip finished on its own during the buffer wait (exit code $($proc.ExitCode)) - track 2 was" -ForegroundColor Yellow
    Write-Host "shorter than expected. Not interrupted; re-run with a bigger fixture to test --resume properly." -ForegroundColor Yellow
    Get-Content $log1
    exit 0
}

Section "Interrupting now (Stop-Process - equivalent to Ctrl+C here, see script header)"
Stop-Process -Id $proc.Id -Force
Start-Sleep -Milliseconds 500   # let the OS finish tearing the process down
Write-Host "Process stopped. Console output from the interrupted pass:"
Get-Content $log1

Section "Checking what the interrupted pass left behind"
$checkpoint = "bigtest.cdi.ripstate.json"
$track1Tmp = Get-ChildItem bigtest.cdi.track*.tmp -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -First 1
if (-not (Test-Path $checkpoint)) { throw "No $checkpoint was left behind - track 1 may not have actually finished before the kill. Try a longer -Track1TimeoutSeconds or re-run." }
Write-Host "Checkpoint present: $checkpoint"
Get-Content $checkpoint
if ($track1Tmp) { Write-Host "Track temp file present: $($track1Tmp.Name) ($($track1Tmp.Length) bytes)" }
else { Write-Host "No track*.tmp file found (unexpected - track 1 should have left one)." -ForegroundColor Yellow }

Section "Pass 2: re-running with --resume, should reuse track 1 instead of re-reading it"
$log2 = "bigtest.cdi.pass2.log"
& $dforge read-cdi $Drive bigtest.cdi --resume 2>&1 | Tee-Object -FilePath $log2
$exit2 = $LASTEXITCODE

Section "Verdict"
$reused = Select-String -Path $log2 -Pattern 'reusing previous capture' -Quiet
$doneCleanly = Select-String -Path $log2 -Pattern 'Done - every sector read cleanly' -Quiet
$finalExists = Test-Path bigtest.cdi

Write-Host "track 1 reused (not re-read):  $reused"
Write-Host "pass 2 finished cleanly:       $doneCleanly"
Write-Host "bigtest.cdi exists:            $finalExists"
Write-Host "pass 2 exit code:              $exit2"
Write-Host ""
if ($reused -and $doneCleanly -and $finalExists -and $exit2 -eq 0) {
    Write-Host "PASS - --resume correctly skipped re-reading the already-completed track after a real interruption." -ForegroundColor Green
} else {
    Write-Host "FAIL (or inconclusive) - paste this whole console output back, plus $log1 and $log2, for a look." -ForegroundColor Red
}
