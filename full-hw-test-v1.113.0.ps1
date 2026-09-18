#Requires -Version 5.1
<#
    full-hw-test-v1.113.0.ps1 - the single, no-cd-required, no-two-commands-to-remember script for
    the v1.113.0 hardware confirmation pass.

    Earlier scripts (build-and-package.ps1, hw-test-v1.113.0.ps1) both work correctly regardless of
    your current directory - they anchor on $PSScriptRoot, not $PWD - but that only helps once you've
    actually invoked them with a path PowerShell can resolve. Typing ".\build-and-package.ps1" while
    sitting in C:\Windows\system32 fails because ".\" means "current folder", and PowerShell doesn't
    search other folders for a script the way it searches PATH for a program. That's what happened.

    This script fixes that by being the ONE thing you run, with its own full path, from anywhere -
    Start Menu "Run", a desktop shortcut, a scheduled task, whatever - and it does the rest itself:
    it builds, then runs the full hardware checklist, without you needing to `cd` anywhere first or
    remember more than one command.

    Usage (from literally anywhere - PowerShell, Run dialog, a shortcut's Target field):

        powershell -ExecutionPolicy Bypass -File "C:\dev\DiscForge\full-hw-test-v1.113.0.ps1" -Drive D:

    Or, if you ARE sitting in C:\dev\DiscForge already, the short form works too:

        .\full-hw-test-v1.113.0.ps1 -Drive D:

    Add -Drive2 E: as well if you have a second recorder free, to also walk the multi-drive step.
    Add -SkipBuild if you already built moments ago and just want to re-run the hardware checklist.

    What it does, in order:
      1. Locates the repo from its OWN path ($PSScriptRoot) - never from whatever folder your
         terminal happened to be sitting in.
      2. Runs build-and-package.ps1 as a genuinely separate child process (not by dot-sourcing or
         calling it in-process) and waits for it to finish, checking its real exit code. This is
         deliberate: a called .ps1 that hits `exit` internally can, in some PowerShell hosts, tear
         down the CALLING session too if it's invoked in-process rather than as its own process -
         not worth risking on a script you're about to leave unattended to build.
      3. Confirms the build actually produced publish\DiscForge.exe and publish\dforge.exe.
      4. Runs the CLI burn+verify smoke test against your DVD+R.
      5. Walks you through the GUI-only manual steps (Quick Burn, media-ID/speed outcome,
         no-drive-detected state, BurnView regression check, and multi-drive if -Drive2 is given) -
         these live only in WinForms, so no script anywhere can automate them; this one pauses and
         tells you exactly what to click and what to look for at each step.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Drive,
    [string]$Drive2 = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

# Anchor everything on where THIS script actually lives, never on the caller's current directory.
$repo = $PSScriptRoot
Write-Host "Repo folder (from this script's own location): $repo"

function Section($title) { Write-Host ""; Write-Host "==== $title ====" -ForegroundColor Cyan }
function Fail($m) { Write-Host "ERROR: $m" -ForegroundColor Red; exit 1 }
function Pause-ForManualStep([string]$instructions) {
    Write-Host ""
    Write-Host "---- MANUAL STEP (GUI-only, can't be scripted from here) ----" -ForegroundColor Yellow
    Write-Host $instructions -ForegroundColor Yellow
    Read-Host "Press Enter once you've done this and noted the result"
}

$buildScript = Join-Path $repo "build-and-package.ps1"
if (-not (Test-Path $buildScript)) { Fail "build-and-package.ps1 not found next to this script in '$repo'." }

if ($SkipBuild) {
    Write-Host "-SkipBuild given - not rebuilding. Using whatever is already in .\publish\." -ForegroundColor DarkYellow
} else {
    Section "1. Build (running build-and-package.ps1 as its own process - this can take a while)"
    # A genuinely separate process via powershell.exe, not '& $buildScript' in this same session -
    # see the header comment for why. -Wait blocks until it's done; we then read its real exit code.
    $proc = Start-Process -FilePath "powershell.exe" `
        -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$buildScript`"", "-Repo", "`"$repo`"") `
        -WorkingDirectory $repo -NoNewWindow -PassThru -Wait
    if ($proc.ExitCode -ne 0) { Fail "build-and-package.ps1 exited with code $($proc.ExitCode) - see the build output above/in that window." }
    Write-Host "Build finished successfully." -ForegroundColor Green
}

$letter = $Drive.TrimEnd(':', '\', '/')

Section "2. Confirm the build is there"
$appExe = Join-Path $repo "publish\DiscForge.exe"
$dforge = Join-Path $repo "publish\dforge.exe"
if (-not (Test-Path $appExe) -or -not (Test-Path $dforge)) {
    Fail "No build found under $repo\publish\. Re-run without -SkipBuild, or run build-and-package.ps1 by hand first."
}
$built = (Get-Item $appExe).LastWriteTime
Write-Host "Found build: $appExe"
Write-Host "  Built: $built  ($([Math]::Round(((Get-Date) - $built).TotalMinutes)) minutes ago)"

Section "3. Drive detection"
& $dforge drives
if ($LASTEXITCODE -ne 0) { Fail "`dforge drives` failed - check the drive is connected before continuing." }

Section "4. Build a small test payload (content doesn't matter, only that write+verify succeeds)"
$isoPath = Join-Path $repo "hwtest-v1.113.0.iso"
if (-not (Test-Path $isoPath)) {
    $bytes = New-Object byte[] (4 * 1024 * 1024)
    (New-Object Random(1113)).NextBytes($bytes)
    [IO.File]::WriteAllBytes($isoPath, $bytes)
}
Write-Host "Test payload: $isoPath ($((Get-Item $isoPath).Length) bytes)"

Section "5. CLI burn + verify smoke test (drive ${letter}:)"
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
Write-Host "Build: PASS (or skipped via -SkipBuild)." -ForegroundColor Green
Write-Host "CLI burn+verify: PASS (confirmed above)." -ForegroundColor Green
Write-Host "Report back what you saw on each manual step above, especially the media-ID outcome (a) vs (b) and the BurnView regression check." -ForegroundColor Cyan
