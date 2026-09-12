# DiscForge - housekeeping: actions what CAN be automated from the current to-do list
# (docs/NEXT.md, "## Housekeeping (user-side, minutes)"), and reports + opens what
# genuinely needs a human (logins, web forms, a file you haven't generated yet).
#
# Usage (PowerShell, from C:\dev\DiscForge - note the .\):
#   .\housekeeping.ps1                  run every check/action below
#   .\housekeeping.ps1 -WhatIf          report only - uninstalls/changes nothing
#   .\housekeeping.ps1 -SkipUninstall   skip the old-DiscForge-1.65 removal step
#   .\housekeeping.ps1 -SkipRelease     skip the GitHub release/workflow check (needs internet)
#   .\housekeeping.ps1 -NoBrowser       don't open any browser tabs; print URLs instead
#
# Covers, in order:
#   1. PATH shadowing: old "DiscForge 1.65" in Program Files shadowing the dev `dforge`.
#      Detects it via the Windows uninstall registry and, if you confirm (or without
#      -WhatIf), runs its uninstaller. This is the one item that actually removes
#      something, so it always asks first unless you already answered with -WhatIf.
#   2. v1.66.0 GitHub Release: checks the tag, the release, and the Release workflow's
#      last run conclusion via the public GitHub API (unauthenticated, read-only).
#   3. COPTR + awesome-list submissions: opens docs/registry-submissions.md (the
#      paste-ready text) plus the COPTR homepage and the awesome-list's GitHub edit
#      page. Submitting itself needs your login/PR, so this can only prep and open.
#   4. AaruFormat interop cross-check: reports what's still needed (a real
#      Aaru-generated .aaruf) - nothing to check yet without one.
#   5. redump.org hash check for ps2game.iso: opens the PS2 disc list (redump.org
#      blocks automated fetches, so the search itself has to be you) and prints the
#      MD5 to paste in.
#
# Independent of build.ps1 / install-cli.ps1 - doesn't touch the build.

param(
    [switch] $WhatIf,
    [switch] $SkipUninstall,
    [switch] $SkipRelease,
    [switch] $NoBrowser
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }

function Open-Url([string]$url) {
    if ($NoBrowser) { Write-Host "  $url" -ForegroundColor DarkGray; return }
    try { Start-Process $url | Out-Null } catch { Write-Host "  (couldn't open a browser - here's the link) $url" -ForegroundColor DarkGray }
}

Write-Host ""
Write-Host "== DiscForge housekeeping ==" -ForegroundColor Cyan
Write-Host "See docs\NEXT.md 'Housekeeping (user-side, minutes)' for the source list."

# ------------------------------------------------------------------------------
# 1) Old "DiscForge 1.65" in Program Files shadowing `dforge` on PATH
# ------------------------------------------------------------------------------
Write-Host ""
Write-Host "-- 1. PATH shadowing: old DiscForge install --" -ForegroundColor Cyan

if ($SkipUninstall) {
    Write-Host "Skipped (-SkipUninstall)." -ForegroundColor Yellow
} else {
    # Report every dforge.exe on PATH and which one actually wins, same check
    # build.ps1/install-cli.ps1 already warn about at build/install time.
    $onPath = Get-Command dforge -All -ErrorAction SilentlyContinue
    if ($onPath) {
        Write-Host "dforge.exe found on PATH ($($onPath.Count)):"
        foreach ($c in $onPath) { Write-Host "  $($c.Source)" }
        Write-Host ("  -> the one that actually runs when you type `dforge`: " + $onPath[0].Source) -ForegroundColor Yellow
    } else {
        Write-Host "No dforge.exe currently resolves on PATH." -ForegroundColor DarkGray
    }

    # Find the old install via the Windows uninstall registry (both 32- and 64-bit
    # views, both HKLM and HKCU, since we don't know how 1.65 was installed).
    $uninstallRoots = @(
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    $old = Get-ItemProperty $uninstallRoots -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like 'DiscForge*' -and $_.DisplayVersion -like '1.65*' }

    if (-not $old) {
        # Version string might not say 1.65 exactly, or might be absent - fall back
        # to anything named DiscForge that ISN'T obviously this dev checkout.
        $old = Get-ItemProperty $uninstallRoots -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like 'DiscForge*' }
    }

    if (-not $old) {
        Write-Host "No 'DiscForge' entry found in the uninstall registry - maybe already gone, or it was a portable/zip install (delete the folder manually)." -ForegroundColor DarkGray
    } else {
        foreach ($entry in $old) {
            Write-Host ""
            Write-Host "Found: $($entry.DisplayName) $($entry.DisplayVersion)" -ForegroundColor Yellow
            Write-Host "  install location: $($entry.InstallLocation)"
            Write-Host "  uninstall string: $($entry.UninstallString)"

            if ($WhatIf) {
                Write-Host "  -WhatIf: not uninstalling. Re-run without -WhatIf, or run this uninstaller yourself." -ForegroundColor DarkGray
                continue
            }
            if (-not $entry.UninstallString) {
                Write-Host "  No uninstall string recorded - remove it from Program Files / Apps & Features manually." -ForegroundColor Red
                continue
            }

            $answer = Read-Host "  Run this uninstaller now? [y/N]"
            if ($answer -notmatch '^[Yy]') { Write-Host "  Skipped." -ForegroundColor DarkGray; continue }

            try {
                # Inno Setup / MSI uninstall strings are usually 'path /args' or
                # 'MsiExec.exe /X{guid}'; Start-Process needs the exe and args split.
                if ($entry.UninstallString -match '^"([^"]+)"\s*(.*)$') {
                    $exe = $Matches[1]; $rest = $Matches[2]
                } elseif ($entry.UninstallString -match '^(\S+)\s*(.*)$') {
                    $exe = $Matches[1]; $rest = $Matches[2]
                } else {
                    $exe = $entry.UninstallString; $rest = ''
                }
                Write-Host "  Launching uninstaller (you may see its own prompt/UAC)..." -ForegroundColor Cyan
                if ($rest) { Start-Process -FilePath $exe -ArgumentList $rest -Wait } else { Start-Process -FilePath $exe -Wait }
                Write-Host "  Uninstaller finished. Re-open a new PowerShell window and check 'Get-Command dforge -All' to confirm PATH is clean." -ForegroundColor Green
            } catch {
                Write-Host "  Couldn't launch the uninstaller automatically: $_" -ForegroundColor Red
                Write-Host "  Remove it via Settings > Apps, or Control Panel > Programs and Features, instead." -ForegroundColor Yellow
            }
        }
    }
}

# ------------------------------------------------------------------------------
# 2) v1.66.0 GitHub Release: tag, release, and the Release workflow's last run
# ------------------------------------------------------------------------------
Write-Host ""
Write-Host "-- 2. v1.66.0 GitHub Release status --" -ForegroundColor Cyan

if ($SkipRelease) {
    Write-Host "Skipped (-SkipRelease)." -ForegroundColor Yellow
} else {
    $repo = 'MatRIXTEaM-code/DiscForge'
    $headers = @{ 'User-Agent' = 'DiscForge-housekeeping-script'; 'Accept' = 'application/vnd.github+json' }
    try {
        $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/tags/v1.66.0" -Headers $headers -ErrorAction Stop
        Write-Host "Release v1.66.0 exists: $($rel.html_url)" -ForegroundColor Green
        Write-Host ("  published: " + $rel.published_at + "   draft: " + $rel.draft + "   body length: " + ($rel.body | Measure-Object -Character).Characters + " chars")
        if (-not $rel.body -or $rel.body.Trim().Length -eq 0) {
            Write-Host "  Release description looks EMPTY - this is the 'paste release notes in' step from NEXT.md, still pending." -ForegroundColor Yellow
        } else {
            Write-Host "  Release description is non-empty - looks like the notes are already pasted in." -ForegroundColor Green
        }
    } catch {
        Write-Host "Couldn't fetch release v1.66.0 (not published yet, or no internet from this window): $($_.Exception.Message)" -ForegroundColor Yellow
    }

    try {
        $runs = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/actions/workflows/release.yml/runs?per_page=5" -Headers $headers -ErrorAction Stop
        $forTag = $runs.workflow_runs | Where-Object { $_.head_branch -eq 'v1.66.0' -or $_.display_title -like '*1.66.0*' } | Select-Object -First 1
        $latest = if ($forTag) { $forTag } else { $runs.workflow_runs | Select-Object -First 1 }
        if ($latest) {
            Write-Host "Latest matching Release workflow run: status=$($latest.status) conclusion=$($latest.conclusion)"
            Write-Host "  $($latest.html_url)"
            if ($latest.conclusion -eq 'success') { Write-Host "  Green. Nothing to chase here." -ForegroundColor Green }
            elseif ($latest.status -ne 'completed') { Write-Host "  Still running or queued - check back." -ForegroundColor Yellow }
            else { Write-Host "  NOT green (conclusion: $($latest.conclusion)) - worth a look." -ForegroundColor Red }
        } else {
            Write-Host "No Release workflow runs found yet." -ForegroundColor Yellow
        }
    } catch {
        Write-Host "Couldn't fetch Release workflow runs: $($_.Exception.Message)" -ForegroundColor Yellow
    }

    if (-not $NoBrowser) {
        Open-Url "https://github.com/$repo/releases/tag/v1.66.0"
        Open-Url "https://github.com/$repo/actions/workflows/release.yml"
    }
}

# ------------------------------------------------------------------------------
# 3) COPTR + awesome-list submissions - prep and open, can't submit for you
# ------------------------------------------------------------------------------
Write-Host ""
Write-Host "-- 3. COPTR + awesome-list submissions --" -ForegroundColor Cyan
$submissionDoc = Join-Path $root 'docs\registry-submissions.md'
if (Test-Path $submissionDoc) {
    Write-Host "Paste-ready text is in: $submissionDoc"
    if (-not $NoBrowser) {
        try { Start-Process $submissionDoc | Out-Null } catch { }
    }
} else {
    Write-Host "docs\registry-submissions.md not found - did the repo sync correctly?" -ForegroundColor Red
}
Write-Host "Opening the two destinations - paste from the doc above into each:"
Open-Url "https://coptr.digipres.org/"
Open-Url "https://github.com/digipres/awesome-digital-preservation/edit/main/README.md"

# ------------------------------------------------------------------------------
# 4) AaruFormat interop cross-check - nothing to run yet
# ------------------------------------------------------------------------------
Write-Host ""
Write-Host "-- 4. AaruFormat interop cross-check --" -ForegroundColor Cyan
Write-Host "Needs a real Aaru-generated .aaruf file to compare against - none is present in this checkout." -ForegroundColor DarkGray
Write-Host "Once you have one: dforge aaruf-info <file.aaruf>  (and/or aaruf-verify, if that's what you're comparing)." -ForegroundColor DarkGray

# ------------------------------------------------------------------------------
# 5) redump.org hash check for ps2game.iso
# ------------------------------------------------------------------------------
Write-Host ""
Write-Host "-- 5. redump.org hash check: ps2game.iso --" -ForegroundColor Cyan
Write-Host "MD5: 30255F8E8958A963212CA6455BB29EE0  (copy this into redump's search box)"
Write-Host "redump.org blocks automated fetches, so this one has to be you." -ForegroundColor DarkGray
Open-Url "https://redump.org/discs/system/ps2/"

Write-Host ""
Write-Host "== Done ==" -ForegroundColor Green
