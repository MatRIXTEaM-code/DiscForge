# DiscForge — the one-stop launcher: check, build, (optionally test), refresh the CLI, start the app.
#
# Usage, from the repo root (or double-click launch-discforge.bat):
#   .\launch-discforge.ps1                 # build Release, refresh dforge CLI if installed, launch GUI
#   .\launch-discforge.ps1 -Quick         # skip the build if the app exe already exists — fastest start
#   .\launch-discforge.ps1 -Test          # run the full test suite before launching (aborts on red)
#   .\launch-discforge.ps1 -NoCli         # don't touch the installed dforge CLI
#   .\launch-discforge.ps1 -NoLaunch      # do everything except start the GUI (CI / refresh-only)
#   .\launch-discforge.ps1 -Configuration Debug
#
# What it does, in order:
#   1. Verifies the .NET 8 SDK is available.
#   2. Builds DiscForge.App (the GUI pulls in Core + Devices transitively)  [skipped by -Quick if built]
#   3. Optionally runs the xUnit suite (-Test).
#   4. Refreshes the installed dforge CLI at C:\tools\dforge IF that install exists, so the
#      command-line tool on your PATH is never older than the app you're looking at.
#      (A stale PATH copy silently missing new commands has bitten us before.)
#   5. Warns if a DIFFERENT dforge.exe on PATH would shadow the refreshed one.
#   6. Launches the GUI.

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$Quick,
    [switch]$Test,
    [switch]$NoCli,
    [switch]$NoLaunch,
    [string]$CliDest = 'C:\tools\dforge'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $root

function Step($msg)  { Write-Host "==> $msg" -ForegroundColor Cyan }
function Note($msg)  { Write-Host "    $msg" -ForegroundColor DarkGray }
function Ok($msg)    { Write-Host "    $msg" -ForegroundColor Green }
function Warn($msg)  { Write-Host "    WARNING: $msg" -ForegroundColor Yellow }
function Die($msg)   { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

Write-Host ''
Write-Host 'DiscForge launcher' -ForegroundColor White
Note "repo: $root   configuration: $Configuration"

# ---- 1. environment ---------------------------------------------------------

Step 'Checking .NET SDK'
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Die 'The .NET SDK is not on PATH. Install .NET 8 from https://dot.net and re-run.' }
$sdkVer = (& dotnet --version) 2>$null
if (-not $sdkVer -or [int]($sdkVer.Split('.')[0]) -lt 8) { Die "Need .NET SDK 8+, found '$sdkVer'." }
Note ".NET SDK $sdkVer"

$appExe = Join-Path $root "src\DiscForge.App\bin\$Configuration\net8.0-windows\DiscForge.exe"

# ---- 2. build ---------------------------------------------------------------

if ($Quick -and (Test-Path $appExe)) {
    Step 'Quick mode: using the existing build'
    Note ((Get-Item $appExe).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss') + '  ' + $appExe)
}
else {
    Step 'Building the GUI app (pulls in Core + Devices)'
    dotnet build (Join-Path $root 'src\DiscForge.App\DiscForge.App.csproj') -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) { Die 'GUI build failed — fix the errors above and re-run.' }
    if (-not (Test-Path $appExe)) { Die "Build reported success but the app exe is missing at: $appExe" }
    Ok "built $appExe"
}

# ---- 3. tests (optional) ----------------------------------------------------

if ($Test) {
    Step 'Running the test suite'
    dotnet test (Join-Path $root 'tests\DiscForge.Core.Tests\DiscForge.Core.Tests.csproj') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { Die 'Tests failed — not launching an app that just went red.' }
    Ok 'all tests green'
}

# ---- 4. refresh the installed CLI ------------------------------------------

if (-not $NoCli) {
    if (Test-Path (Join-Path $CliDest 'dforge.exe')) {
        Step "Refreshing the dforge CLI at $CliDest (so PATH never runs a stale copy)"
        dotnet publish (Join-Path $root 'src\DiscForge.Cli\DiscForge.Cli.csproj') `
            -c $Configuration -f net8.0-windows -o $CliDest --nologo -v q
        if ($LASTEXITCODE -ne 0) { Die 'CLI publish failed.' }
        Ok ("dforge refreshed at " + (Get-Item (Join-Path $CliDest 'dforge.exe')).LastWriteTime.ToString('HH:mm:ss'))
    }
    else {
        Note "No CLI install at $CliDest — skipping refresh. Run .\install-cli.ps1 once to set it up."
    }

    # 5. Shadow check: would PATH run a DIFFERENT dforge than the one we just refreshed?
    $onPath = Get-Command dforge.exe -ErrorAction SilentlyContinue
    if ($onPath -and (Test-Path (Join-Path $CliDest 'dforge.exe'))) {
        $resolved = [IO.Path]::GetFullPath($onPath.Source)
        $expected = [IO.Path]::GetFullPath((Join-Path $CliDest 'dforge.exe'))
        if ($resolved -ne $expected) {
            Warn "PATH runs dforge from '$resolved', not '$expected' — that copy may be stale."
            Warn 'Fix your PATH order, or call the full path explicitly.'
        }
    }
}

# ---- 6. launch --------------------------------------------------------------

$ver = (Get-Item $appExe).VersionInfo.ProductVersion
if ($NoLaunch) {
    Step "Done (NoLaunch): DiscForge $ver is ready at $appExe"
    exit 0
}

Step "Launching DiscForge $ver"
Start-Process -FilePath $appExe -WorkingDirectory (Split-Path $appExe)
Ok 'app started — this window can be closed.'
