#Requires -Version 5.1
<#
    build-and-package.ps1 - one-shot build, test, and installer script for DiscForge.

    This is a thin convenience wrapper around the existing build-app.ps1 (which already does
    everything: solution build, tests, self-contained publish, and Inno Setup compile). It exists
    so there's a single, no-flags-to-remember entry point for "build it and give me the installer" -
    the exact thing needed after the Quick Burn / BurnExecutor refactor and multi-drive changes,
    since those need a real compile (this sandbox can only syntax-check, not compile).

    Usage (from the repo root, on a Windows machine with the .NET 8 SDK):

        .\build-and-package.ps1

    That runs, in order:
      1. dotnet build the whole solution (Core, CLI, App, tests) - Release config.
      2. dotnet test - the full xUnit suite (this is where a bad BurnExecutor refactor would show up
         as a compile/test failure rather than a silent runtime bug).
      3. Self-contained publish to .\publish\ (win-x64, bundles the .NET runtime).
      4. Installer compile via Inno Setup's ISCC.exe -> installer\Output\DiscForge-Setup-<version>.exe
         (skipped with a clear message if Inno Setup 6 isn't installed - the publish folder is still
         produced either way).

    Prerequisites:
      - .NET 8 SDK           https://dotnet.microsoft.com/download/dotnet/8.0
      - Inno Setup 6         https://jrsoftware.org/isdl.php   (only needed for the installer .exe;
                              the app itself builds and runs fine without it)

    After this finishes, do the hardware/manual pass that's still outstanding:
      - Launch .\publish\DiscForge.exe (or the freshly built app under
        src\DiscForge.App\bin\Release\net8.0-windows\DiscForge.exe) and open the new
        "Quick Burn" tile (top-left area, right after "Record Disc").
      - Burn a real ISO through Quick Burn to a spare disc and confirm Verify passes.
      - With nothing plugged in, confirm Quick Burn's drive dropdown shows a plain "no recorder
        detected" state rather than an empty/confusing dropdown.
      - If you have two optical drives, re-confirm BurnView's "burn to several drives at once"
        checkboxes still work after the BurnExecutor refactor - that's the highest-risk change
        in this pass since it rewired BurnView's entire execution path.
      - Re-run BurnView's existing single-burn and queue flows once each, to confirm they still
        behave exactly as before (same log lines, same confirmation prompts).

    This script does not replace build-app.ps1 - it just calls it with the flags that matter for
    this pass (-Test -Publish), so you don't have to remember them.
#>
[CmdletBinding()]
param(
    [string]$Repo = $PSScriptRoot,
    [switch]$Run,
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'

function Fail($m) { Write-Host "ERROR: $m" -ForegroundColor Red; exit 1 }
function Step($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }

if (-not (Test-Path (Join-Path $Repo 'DiscForge.sln'))) {
    Fail "No DiscForge.sln found in '$Repo'. Pass -Repo <path> if this script isn't sitting in the repo root."
}

$buildApp = Join-Path $Repo 'build-app.ps1'
if (-not (Test-Path $buildApp)) { Fail "build-app.ps1 not found next to this script in '$Repo'." }

Step 'Tooling check'
try { $sdk = (& dotnet --version).Trim() } catch { Fail 'The .NET SDK (`dotnet`) was not found on PATH. Install the .NET 8 SDK first.' }
Write-Host ".NET SDK: $sdk" -ForegroundColor DarkGray

$isccOnPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
$isccCandidates = @(
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
)
$isccFound = $isccOnPath -or ($isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1)
if ($isccFound) {
    Write-Host 'Inno Setup 6: found - the installer .exe will be produced.' -ForegroundColor DarkGray
} else {
    Write-Host 'Inno Setup 6: NOT found - the self-contained payload will still be built to .\publish\,' -ForegroundColor DarkYellow
    Write-Host '  but installer\Output\DiscForge-Setup-<version>.exe will NOT be produced this run.' -ForegroundColor DarkYellow
    Write-Host '  Install it from https://jrsoftware.org/isdl.php and re-run this script to get the installer.' -ForegroundColor DarkYellow
}

Step 'Build + test + publish + package (delegating to build-app.ps1)'
# Explicit calls rather than array-splatting: array-splatting a mix of named strings and
# bare switch tokens (e.g. '-Test') has been seen to misbind on some PowerShell builds
# ("A positional parameter cannot be found that accepts argument '-Test'"), so this avoids
# that class of bug entirely rather than debugging splatting semantics blind.
if ($Run) {
    & $buildApp -Repo $Repo -Test -Publish -Run -Configuration $Configuration
} else {
    & $buildApp -Repo $Repo -Test -Publish -Configuration $Configuration
}
if ($LASTEXITCODE -ne 0) { Fail 'build-app.ps1 reported a failure - see the output above.' }

Step 'Done'
$setupExe = Get-ChildItem (Join-Path $Repo 'installer\Output') -Filter 'DiscForge-Setup-*.exe' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($setupExe) {
    Write-Host "Installer ready:  $($setupExe.FullName)" -ForegroundColor Green
} else {
    Write-Host "No installer .exe produced (see the Inno Setup note above). Self-contained app is in .\publish\." -ForegroundColor DarkYellow
}
Write-Host "`nNext: run the manual Quick Burn / multi-drive checklist in this script's header comment." -ForegroundColor Cyan
