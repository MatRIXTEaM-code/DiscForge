#Requires -Version 5.1
<#
.SYNOPSIS
    One script for the whole routine: push to GitHub, build the app and installer, then start
    DiscForge and open the donation page so you can check both.

.DESCRIPTION
    Steps:
      1. Finds your DiscForge folder (this script's folder, then C:\dev\DiscForge, then asks).
      2. Git: shows what's changed, offers to commit anything uncommitted, then pushes to GitHub.
      3. Build: runs build-discforge.ps1 (build, tests, app .exe, installer .exe).
      4. Starts the freshly built DiscForge (publish\DiscForge.exe). Open the About tile and
         check the "Donate (PayPal)" button is there.
      5. Opens https://paypal.me/DiscForgeUK in your browser so you can see what donors see.

    Options:
      -SkipPush     don't push to GitHub
      -SkipTests    build without running the tests (faster)
      -SkipBuild    only push (no build)
      -NoLaunch     don't start the app or open the PayPal page afterwards

    The window waits for Enter at the end, so nothing flashes past. Plain ASCII on purpose, so
    Windows PowerShell 5.1 reads it correctly.

.EXAMPLE
    .\do-everything.ps1
.EXAMPLE
    .\do-everything.ps1 -SkipTests
#>
[CmdletBinding()]
param(
    [string]$Repo = $PSScriptRoot,
    [switch]$SkipPush,
    [switch]$SkipTests,
    [switch]$SkipBuild,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$DonateUrl = 'https://paypal.me/DiscForgeUK'
$script:Problems = New-Object System.Collections.Generic.List[string]

function Write-Step([string]$Text) { Write-Host ''; Write-Host "=== $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host "  OK   $Text" -ForegroundColor Green }
function Write-Info([string]$Text) { Write-Host "       $Text" -ForegroundColor Gray }
function Write-Bad([string]$Text)  { Write-Host "  !!   $Text" -ForegroundColor Red; $script:Problems.Add($Text) }

# Runs git with the given arguments; stderr is shown, not treated as a script error
# (git writes normal progress to stderr, which PowerShell 5.1 would otherwise turn into errors).
function Invoke-Git([string[]]$GitArgs) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & git @GitArgs 2>&1 | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.ToString() } else { $_ }
        } | Out-Host
        return $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $old }
}

function Get-GitText([string[]]$GitArgs) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { return ((& git @GitArgs 2>$null) -join "`n").Trim() }
    finally { $ErrorActionPreference = $old }
}

function Pause-End {
    Write-Host ''
    [void](Read-Host 'Press Enter to close')
}

try {
    # ---------------------------------------------------------------- 1. Find the repo
    Write-Step '1/5  Finding your DiscForge folder'
    function Test-RepoRoot([string]$Path) { return $Path -and (Test-Path (Join-Path $Path 'DiscForge.sln')) }
    $found = @($Repo, (Get-Location).Path, 'C:\dev\DiscForge') | Where-Object { Test-RepoRoot $_ } | Select-Object -First 1
    if (-not $found) {
        Add-Type -AssemblyName System.Windows.Forms | Out-Null
        $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
        $dlg.Description = 'Select your DiscForge folder (the one containing DiscForge.sln)'
        if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK -and (Test-RepoRoot $dlg.SelectedPath)) {
            $found = $dlg.SelectedPath
        }
    }
    if (-not $found) { throw "Couldn't find DiscForge.sln. Put this script in C:\dev\DiscForge, or pass -Repo <folder>." }
    $Repo = (Resolve-Path $found).Path
    Set-Location $Repo
    Write-Ok $Repo

    # ---------------------------------------------------------------- 2. Git push
    Write-Step '2/5  Pushing to GitHub'
    if ($SkipPush) {
        Write-Info 'Skipped (-SkipPush).'
    }
    elseif (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Write-Bad 'git is not installed or not on PATH - install Git for Windows (https://git-scm.com), then run this again.'
    }
    else {
        # Leftover lock files from an interrupted git command block everything; clear stale ones.
        foreach ($lock in @('.git\index.lock', '.git\HEAD.lock')) {
            $p = Join-Path $Repo $lock
            if (Test-Path $p) {
                $age = (Get-Date) - (Get-Item $p).LastWriteTime
                if ($age.TotalMinutes -gt 5) { Remove-Item $p -Force; Write-Info "Removed a stale $lock" }
            }
        }

        $branch = Get-GitText @('rev-parse', '--abbrev-ref', 'HEAD')
        Write-Info "Branch: $branch"

        # Uncommitted changes to tracked files?
        $changes = Get-GitText @('status', '--porcelain', '--untracked-files=no')
        if ($changes) {
            Write-Host ''
            Write-Host 'These tracked files have uncommitted changes:' -ForegroundColor Yellow
            Invoke-Git @('status', '--short', '--untracked-files=no') | Out-Null
            $answer = Read-Host 'Commit them before pushing? (y/N)'
            if ($answer -match '^(y|yes)$') {
                $msg = Read-Host 'Commit message (Enter for "Update")'
                if (-not $msg) { $msg = 'Update' }
                if ((Invoke-Git @('commit', '-a', '-m', $msg)) -ne 0) { Write-Bad 'git commit failed (see above).' }
                else { Write-Ok 'Committed.' }
            }
            else { Write-Info 'Left uncommitted - only already-committed work will be pushed.' }
        }

        Invoke-Git @('fetch', '--quiet', 'origin') | Out-Null
        $ahead = Get-GitText @('rev-list', '--count', "origin/$branch..HEAD")
        if (-not $ahead) { $ahead = '?' }
        Write-Info "Commits to push: $ahead"
        if ($ahead -eq '0') {
            Write-Ok 'Nothing to push - GitHub is already up to date.'
        }
        else {
            Invoke-Git @('log', '--oneline', "origin/$branch..HEAD") | Out-Null
            if ((Invoke-Git @('push', 'origin', $branch)) -ne 0) {
                Write-Bad 'git push failed. If a login window appeared, sign in to GitHub and run this again.'
            }
            else { Write-Ok "Pushed $ahead commit(s) to GitHub." }
        }
    }

    # ---------------------------------------------------------------- 3. Build
    Write-Step '3/5  Building the app and installer'
    $built = $false
    if ($SkipBuild) {
        Write-Info 'Skipped (-SkipBuild).'
    }
    else {
        $buildScript = Join-Path $Repo 'build-discforge.ps1'
        if (-not (Test-Path $buildScript)) {
            Write-Bad 'build-discforge.ps1 is missing from the DiscForge folder.'
        }
        else {
            if ($SkipTests) { & $buildScript -Repo $Repo -SkipTests -NoPause }
            else            { & $buildScript -Repo $Repo -NoPause }
            if ($LASTEXITCODE -eq 0) { $built = $true; Write-Ok 'Build finished (details and log path are above).' }
            else { Write-Bad 'The build failed - scroll up for the first error, or open the newest file in build-logs.' }
        }
    }

    # ---------------------------------------------------------------- 4. Start the app
    Write-Step '4/5  Starting DiscForge'
    $appExe = Join-Path $Repo 'publish\DiscForge.exe'
    if ($NoLaunch) { Write-Info 'Skipped (-NoLaunch).' }
    elseif (-not (Test-Path $appExe)) { Write-Bad "No built app at $appExe - the build needs to succeed first." }
    else {
        if (-not $built) { Write-Info 'Note: this is the app from the last successful build.' }
        Start-Process $appExe
        Write-Ok 'DiscForge started (Windows may ask for administrator permission - that is normal).'
        Write-Info 'Check: click the About tile -> the "Donate (PayPal)" button should be there.'
    }

    # ---------------------------------------------------------------- 5. Donation page
    Write-Step '5/5  Opening your donation page'
    if ($NoLaunch) { Write-Info 'Skipped (-NoLaunch).' }
    else {
        Start-Process $DonateUrl
        Write-Ok "Opened $DonateUrl in your browser."
    }
}
catch {
    Write-Bad $_.Exception.Message
}

Write-Host ''
Write-Host '=================================================================' -ForegroundColor White
if ($script:Problems.Count -eq 0) {
    Write-Host '  ALL DONE - pushed, built, and ready to check.' -ForegroundColor Green
    $installer = Get-ChildItem (Join-Path $Repo 'installer\Output') -Filter 'DiscForge-Setup-*.exe' -ErrorAction SilentlyContinue |
                 Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($installer) { Write-Host "  Installer: $($installer.FullName)" -ForegroundColor Green }
}
else {
    Write-Host '  Finished with problems:' -ForegroundColor Red
    foreach ($p in $script:Problems) { Write-Host "    - $p" -ForegroundColor Red }
}
Write-Host '=================================================================' -ForegroundColor White
Pause-End
if ($script:Problems.Count -eq 0) { exit 0 } else { exit 1 }
