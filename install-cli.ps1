# DiscForge - publish the dforge CLI and put it on your PATH.
#
# Builds src\DiscForge.Cli into a folder of your choosing (default C:\tools\dforge),
# then adds that folder to your USER PATH so `dforge` works from any window.
#
# It also:
#   * can publish a self-contained build (bundles the .NET runtime) for install
#     locations that should not depend on a system-wide .NET (e.g. Program Files);
#   * can clean the target folder first, with a guard so it never deletes a GUI
#     install that happens to share the folder;
#   * detects the "half-and-half" state that produces 'No frameworks were found'
#     (a framework-dependent publish laid on top of an older self-contained one)
#     and refuses to recreate it;
#   * warns when ANOTHER dforge.exe already on your PATH would run instead of the
#     copy it just built.
#
# Usage (from C:\dev\DiscForge):
#   .\install-cli.ps1                          # publish to C:\tools\dforge, add to PATH
#   .\install-cli.ps1 -Dest D:\bin\dforge      # publish somewhere else
#   .\install-cli.ps1 -SelfContained           # bundle the runtime (no system .NET needed)
#   .\install-cli.ps1 -Clean                    # empty the target folder first (guarded)
#   .\install-cli.ps1 -NoPath                   # publish only, don't touch PATH
#
# For a Program Files install, run an elevated PowerShell and use -SelfContained:
#   .\install-cli.ps1 -Dest "C:\Program Files\DiscForge" -SelfContained

param(
    [string] $Dest = 'C:\tools\dforge',
    [switch] $NoPath,
    [switch] $SelfContained,
    [switch] $Clean,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }
$proj = Join-Path $root 'src\DiscForge.Cli\DiscForge.Cli.csproj'

if (-not (Test-Path $proj)) {
    Write-Host "Can't find $proj - run this from C:\dev\DiscForge." -ForegroundColor Red
    exit 1
}

# Files that mean "the DiscForge GUI also lives here" - we must not delete these.
$guiMarkers = @('DiscForge.exe', 'DiscForge.App.dll')
$hasGui = $false
foreach ($mk in $guiMarkers) {
    if (Test-Path (Join-Path $Dest $mk)) { $hasGui = $true }
}

$hostfxr = Join-Path $Dest 'hostfxr.dll'
$hasBundledRuntime = Test-Path $hostfxr

# --- Guard: never recreate the half-and-half install --------------------------
# A framework-dependent publish on top of a folder that still has a bundled runtime
# (hostfxr.dll) yields 'No frameworks were found'. Stop and point at the fix.
if (-not $SelfContained -and -not $Clean -and $hasBundledRuntime) {
    Write-Host ""
    Write-Host "STOP: $Dest already contains a self-contained runtime (hostfxr.dll)." -ForegroundColor Red
    Write-Host "A framework-dependent publish on top of it produces the error:" -ForegroundColor Red
    Write-Host "    'You must install or update .NET ... No frameworks were found.'" -ForegroundColor Red
    Write-Host ""
    Write-Host "Do one of these instead:" -ForegroundColor Cyan
    Write-Host "  * Re-run with -SelfContained   (recommended for this folder - stays standalone)" -ForegroundColor Cyan
    Write-Host "  * Or re-run with -Clean         (empty the folder first, then framework-dependent)" -ForegroundColor Cyan
    exit 1
}

# --- Clean (guarded) ----------------------------------------------------------
if ($Clean -and (Test-Path $Dest)) {
    if ($hasGui -and -not $Force) {
        Write-Host ""
        Write-Host "STOP: $Dest also contains the DiscForge GUI (DiscForge.exe / DiscForge.App.dll)." -ForegroundColor Red
        Write-Host "Cleaning it would delete the GUI too." -ForegroundColor Red
        Write-Host ""
        Write-Host "Safer options:" -ForegroundColor Cyan
        Write-Host "  * Install the CLI to a dedicated folder:  -Dest C:\tools\dforge" -ForegroundColor Cyan
        Write-Host "  * Or fix the runtime without deleting anything:  -SelfContained (no -Clean)" -ForegroundColor Cyan
        Write-Host "  * Or, if you really mean to wipe this folder, add -Force." -ForegroundColor Cyan
        exit 1
    }
    Write-Host "Cleaning $Dest ..." -ForegroundColor DarkGray
    Get-ChildItem -LiteralPath $Dest -Force | Remove-Item -Recurse -Force
}

# --- Publish ------------------------------------------------------------------
# The Cli project now multi-targets net8.0 (no drive/SPTI support) and
# net8.0-windows (the real thing — DiscForge.Devices, live drive access).
# `dotnet publish` on a multi-target project needs an explicit -f or it just
# errors with NETSDK1129 ("must specify one of the following frameworks").
# net8.0-windows is always the right one for the installed CLI.
if ($SelfContained) {
    Write-Host "Publishing dforge (self-contained, win-x64) -> $Dest" -ForegroundColor Cyan
    dotnet publish $proj -c Release -f net8.0-windows -r win-x64 --self-contained true -o $Dest
} else {
    Write-Host "Publishing dforge (framework-dependent) -> $Dest" -ForegroundColor Cyan
    dotnet publish $proj -c Release -f net8.0-windows -o $Dest
}
if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed (exit $LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}

# The project builds as 'dforge', so publish emits dforge.exe directly. Older layouts
# emitted DiscForge.Cli.exe; make a dforge.exe alongside it if that's all we have.
$exe = Join-Path $Dest 'DiscForge.Cli.exe'
$dforge = Join-Path $Dest 'dforge.exe'
if ((Test-Path $exe) -and -not (Test-Path $dforge)) {
    Copy-Item $exe $dforge -Force
    Write-Host "Created dforge.exe alongside DiscForge.Cli.exe" -ForegroundColor DarkGray
}
if (-not (Test-Path $dforge)) {
    Write-Host "Warning: no dforge.exe was produced in $Dest - publish may have changed layout." -ForegroundColor Yellow
}

# --- PATH ---------------------------------------------------------------------
if ($NoPath) {
    Write-Host ""
    Write-Host "Done. CLI is in $Dest (PATH not modified - run it with the full path)." -ForegroundColor Green
} else {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $parts = @()
    if ($userPath) { $parts = $userPath -split ';' | Where-Object { $_ -ne '' } }
    $already = $parts | Where-Object { $_.TrimEnd('\') -ieq $Dest.TrimEnd('\') }

    if ($already) {
        Write-Host ""
        Write-Host "$Dest is already on your PATH." -ForegroundColor Green
    } else {
        $newPath = ($parts + $Dest) -join ';'
        [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
        $env:Path = $env:Path + ';' + $Dest   # so it works in THIS window too
        Write-Host ""
        Write-Host "Added $Dest to your USER PATH." -ForegroundColor Green
        Write-Host "Open a NEW PowerShell window for it to stick." -ForegroundColor Cyan
    }
}

# --- Shadow check: will 'dforge' actually run the copy we just built? ----------
# Machine PATH is searched before User PATH, so an entry there (a Program Files
# install) can shadow a fresh build in a user folder. Report it plainly.
$destNorm = $Dest.TrimEnd('\')
$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
$userPath2   = [Environment]::GetEnvironmentVariable('Path', 'User')

$orderedDirs = @()
foreach ($p in @($machinePath, $userPath2)) {
    if ($p) { $orderedDirs += ($p -split ';' | Where-Object { $_ -ne '' }) }
}

$shadows = @()
foreach ($d in $orderedDirs) {
    $dn = $d.TrimEnd('\')
    if ($dn -ieq $destNorm) { break }   # reached our own entry first: nothing shadows it
    $cand = Join-Path $dn 'dforge.exe'
    if (Test-Path $cand) { $shadows += $cand }
}

if ($shadows.Count -gt 0) {
    $winner = $shadows[0]
    $winnerDir = Split-Path $winner -Parent
    Write-Host ""
    Write-Host "WARNING: another dforge.exe is ahead of $Dest on your PATH:" -ForegroundColor Yellow
    foreach ($s in ($shadows | Select-Object -Unique)) {
        Write-Host "    $s" -ForegroundColor Yellow
    }
    Write-Host "Typing 'dforge' will run:  $winner" -ForegroundColor Yellow
    Write-Host "That is NOT the build you just published, so new commands will look 'unknown'." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Fix it one of these ways:" -ForegroundColor Cyan
    Write-Host "  1. Refresh that copy too (run PowerShell as Administrator if it's under Program Files)." -ForegroundColor Cyan
    Write-Host "     If that folder also holds the GUI, use -SelfContained so the runtime stays consistent:" -ForegroundColor Cyan
    Write-Host "       .\install-cli.ps1 -Dest `"$winnerDir`" -SelfContained" -ForegroundColor White
    Write-Host "  2. Or remove $winnerDir from PATH (or uninstall that older DiscForge) so $Dest wins." -ForegroundColor Cyan
    Write-Host "  3. Or just run this build by full path:" -ForegroundColor Cyan
    Write-Host "       `"$dforge`" <command>" -ForegroundColor White
} else {
    $resolved = Get-Command dforge -ErrorAction SilentlyContinue | Select-Object -First 1
    Write-Host ""
    if ($resolved -and ($resolved.Source)) {
        Write-Host "OK: 'dforge' resolves to $($resolved.Source)" -ForegroundColor Green
    }
    Write-Host "Ready. Try:  dforge help" -ForegroundColor Green
}
