#Requires -Version 5.1
<#
    hw-test-v1.113.0.ps1 - hardware confirmation pass for v1.113.0 (Quick Burn / the BurnExecutor
    refactor that both Quick Burn and the existing Burn screen now share) plus the still-open
    v1.112.0 gap: automatic write-speed selection from a DVD/BD media ID's rated speed. That gap has
    never actually been confirmed on real hardware - the only prior burn-queue confirmation used a
    CD-R, and CD-R's ATIP never carries a speed rating the way a DVD/BD media ID does.

    What this script CAN automate: drive detection and a CLI burn+verify smoke test via `dforge burn`
    - a baseline confirmation that the underlying burn engine itself still works after the refactor.

    What this script CANNOT automate, because the features live only in the WinForms GUI
    (QuickBurnView / BurnView), not the CLI: the Quick Burn tile itself, its no-drive-detected state,
    the auto write-speed-by-media-ID default, and multi-drive simultaneous burning. For those, this
    script pauses at the right moment and tells you exactly what to click and what to look for -
    it does not attempt to fake automating a GUI it can't reach.

    This script does NOT build the app - run .\build-and-package.ps1 yourself first. (Calling a
    script that can `exit` from inside another script is a known PowerShell footgun: `exit` inside a
    called .ps1 can terminate the whole calling session, not just return an error code you can check
    cleanly - not worth risking here.) This script just checks that a build exists and asks you to
    confirm it's the one you want to test.

    Usage:
        .\hw-test-v1.113.0.ps1 -Drive D:
        .\hw-test-v1.113.0.ps1 -Drive D: -Drive2 E:   # also walks through the multi-drive step
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Drive,
    [string]$Drive2 = ""
)

$ErrorActionPreference = "Stop"

# Set-Location only moves PowerShell's own location provider - it does not reliably sync .NET's
# process-wide CurrentDirectory in every host/elevation context, so both are set explicitly (the
# same fix hw-test-resume.ps1 needed for the same reason - see that script's header for the repro).
Set-Location $PSScriptRoot
[Environment]::CurrentDirectory = $PSScriptRoot
Write-Host "Working directory: $PSScriptRoot"

function Section($title) { Write-Host ""; Write-Host "==== $title ====" -ForegroundColor Cyan }
function Fail($m) { Write-Host "ERROR: $m" -ForegroundColor Red; exit 1 }
function Pause-ForManualStep([string]$instructions) {
    Write-Host ""
    Write-Host "---- MANUAL STEP (GUI-only, can't be scripted from here) ----" -ForegroundColor Yellow
    Write-Host $instructions -ForegroundColor Yellow
    Read-Host "Press Enter once you've done this and noted the result"
}

$letter = $Drive.TrimEnd(':', '\', '/')

Section "1. Confirm a build exists"
$appExe = Join-Path $PSScriptRoot "publish\DiscForge.exe"
$dforge = Join-Path $PSScriptRoot "publish\dforge.exe"
if (-not (Test-Path $appExe) -or -not (Test-Path $dforge)) {
    Fail "No build found under .\publish\. Run .\build-and-package.ps1 first, then re-run this script."
}
$built = (Get-Item $appExe).LastWriteTime
Write-Host "Found build: $appExe"
Write-Host "  Built: $built  ($([Math]::Round(((Get-Date) - $built).TotalMinutes)) minutes ago)"
if (((Get-Date) - $built).TotalHours -gt 6) {
    Write-Host "  That build is more than 6 hours old - if you've changed anything since, run" -ForegroundColor DarkYellow
    Write-Host "  .\build-and-package.ps1 again before trusting this test run." -ForegroundColor DarkYellow
}
Read-Host "Press Enter to confirm this is the build you want to test"

Section "2. Drive detection"
& $dforge drives
if ($LASTEXITCODE -ne 0) { Fail "`dforge drives` failed - check the drive is connected before continuing." }

Section "3. Build a small test payload (content doesn't matter, only that write+verify succeeds)"
$isoPath = Join-Path $PSScriptRoot "hwtest-v1.113.0.iso"
if (-not (Test-Path $isoPath)) {
    $bytes = New-Object byte[] (4 * 1024 * 1024)
    (New-Object Random(1113)).NextBytes($bytes)
    [IO.File]::WriteAllBytes($isoPath, $bytes)
}
Write-Host "Test payload: $isoPath ($((Get-Item $isoPath).Length) bytes)"

Section "4. CLI burn + verify smoke test (drive ${letter}:)"
Write-Host "Insert the DVD+R now if it isn't already in the drive." -ForegroundColor Yellow
Read-Host "Press Enter when ready to burn"
& $dforge burn $isoPath $letter --verify
if ($LASTEXITCODE -ne 0) { Fail "CLI burn/verify failed - stop and investigate before doing the GUI steps below." }
Write-Host "CLI burn + verify: PASS" -ForegroundColor Green

Pause-ForManualStep @"
Open $appExe, click the "Quick Burn" tile (right after "Record Disc").
Pick any real ISO, confirm the drive dropdown auto-selected ${letter}:, click Burn, confirm the
"Insert media. Begin the job?" prompt appears, and confirm Verify actually runs and reports success
afterward.
"@

Pause-ForManualStep @"
Still in Quick Burn (or the speed dropdown in the normal Burn screen - either shows the same
underlying result): note what it shows for this DVD+R's write speed. Two acceptable outcomes, per
MediaIdentity.cs's ADIP handling:
  (a) a specific rated speed was found and offered as the default - the auto-speed-by-media-ID gap
      is now genuinely closed on this drive/disc combination; or
  (b) no media ID could be read (DVD+R keeps it in ADIP, which the code doesn't read yet on most
      drives) and it defaulted to Max with no error - that's the documented, correct fallback, not
      a bug.
Either result is useful - note which one you actually saw.
"@

Pause-ForManualStep @"
Unplug the drive (or otherwise make it undetectable), reopen Quick Burn, and confirm it shows a
plain "no recorder detected" message rather than a silently empty drive dropdown. Reconnect the
drive afterward before continuing.
"@

Pause-ForManualStep @"
Open the normal "Record Disc" screen (BurnView) - NOT Quick Burn. Burn something through it the way
you always have, and separately try the burn queue (add 2+ images, run the queue). Both should look
and behave exactly as before. This is the highest-risk regression check of this whole pass: the
BurnExecutor refactor rewired BurnView's entire execution path to share code with Quick Burn.
"@

if ($Drive2) {
    $letter2 = $Drive2.TrimEnd(':', '\', '/')
    Pause-ForManualStep @"
Multi-drive: in BurnView, check the destination boxes for BOTH ${letter}: and ${letter2}:, load one
image, and start the burn. Confirm the "Insert blank media in all N drives. They will be burned
simultaneously." prompt appears, and that both drives actually burn concurrently (not one after the
other). This is the first real hardware confirmation of the RunAllAsync/Task.WhenAll multi-drive
path.
"@
} else {
    Write-Host ""
    Write-Host "No -Drive2 given - skipping the multi-drive step. Re-run with -Drive2 <letter> if a second recorder is free." -ForegroundColor DarkYellow
}

Section "Done"
Write-Host "CLI burn+verify: PASS (confirmed above)." -ForegroundColor Green
Write-Host "Report back what you saw on each manual step above, especially the media-ID outcome (a) vs (b) and the BurnView regression check." -ForegroundColor Cyan
