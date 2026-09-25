#Requires -Version 5.1
<#
.SYNOPSIS
    Builds DiscForge from source and produces the app .exe and the installer .exe, in one go.

.DESCRIPTION
    A complete build script with no other build script needed. From the repo root it:

      1. Checks the tools: .NET 8 SDK (required) and Inno Setup 6 (for the installer).
      2. Closes any DiscForge.exe running from this repo, so files aren't locked.
      3. Restores and builds the whole solution (Release).
      4. Runs the test suite (skip with -SkipTests).
      5. Checks the CLI command docs are in sync (warning only).
      6. Publishes a self-contained win-x64 app to .\publish\. That folder holds
         DiscForge.exe (the GUI) and dforge.exe (the CLI) and runs on a PC with no .NET installed.
      7. Compiles installer\DiscForge.iss with Inno Setup, giving
         installer\Output\DiscForge-Setup-<version>.exe (skip with -NoInstaller).

    Everything is written to the screen AND to build-logs\build-<date>-<time>.txt. The window
    waits for Enter before closing (-NoPause turns that off), so a double-clicked run never
    just flashes and vanishes.

.EXAMPLE
    .\build-discforge.ps1
    Full build, tests, app and installer.

.EXAMPLE
    .\build-discforge.ps1 -SkipTests -Run
    Faster build without tests, then starts the built app.

.NOTES
    Easiest way to run: double-click build-discforge.bat next to this file. It starts
    PowerShell with -ExecutionPolicy Bypass, so script-execution policy can't block it.
    Plain ASCII on purpose: Windows PowerShell 5.1 misreads non-ASCII characters in a script
    saved without a byte-order mark.
#>
[CmdletBinding()]
param(
    [string]$Repo = $PSScriptRoot,
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$NoInstaller,
    [switch]$Clean,
    [switch]$Run,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
$script:StartTime = Get-Date
$script:Failed = $false
$script:Warnings = New-Object System.Collections.Generic.List[string]

# ---------------------------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------------------------

function Write-Step([string]$Text) {
    $elapsed = (Get-Date) - $script:StartTime
    Write-Host ''
    Write-Host ('=== {0}   [{1:mm\:ss}]' -f $Text, $elapsed) -ForegroundColor Cyan
}

function Write-Ok([string]$Text)   { Write-Host "  OK   $Text" -ForegroundColor Green }
function Write-Info([string]$Text) { Write-Host "       $Text" -ForegroundColor Gray }
function Write-Warn([string]$Text) {
    Write-Host "  WARN $Text" -ForegroundColor Yellow
    $script:Warnings.Add($Text)
}

function Stop-Build([string]$Text) {
    # Throwing (rather than 'exit') lets the finally block below stop the log and pause.
    throw "BUILD FAILED: $Text"
}

# Runs a program, streams its output to the screen and the log, and returns its exit code.
# ErrorActionPreference is relaxed while it runs: Windows PowerShell 5.1 would otherwise
# turn anything the program writes to stderr (dotnet's warnings, for one) into a
# terminating error.
function Invoke-Tool([string]$Exe, [string[]]$Arguments) {
    Write-Info ("> " + $Exe + ' ' + ($Arguments -join ' '))
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Exe @Arguments 2>&1 | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.ToString() } else { $_ }
        } | Out-Host
        return $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $old }
}

function Find-InnoSetup {
    $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $candidates = New-Object System.Collections.Generic.List[string]
    $candidates.Add("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe")
    $candidates.Add("$env:ProgramFiles\Inno Setup 6\ISCC.exe")
    $candidates.Add("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")   # per-user install

    # Wherever the Inno Setup installer recorded it (covers custom install folders).
    $keys = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
    )
    foreach ($k in $keys) {
        $loc = (Get-ItemProperty -Path $k -Name 'InstallLocation' -ErrorAction SilentlyContinue).InstallLocation
        if ($loc) { $candidates.Add((Join-Path $loc 'ISCC.exe')) }
    }
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
    return $null
}

function Stop-RunningDiscForge([string]$Root) {
    $procs = @(Get-Process -Name 'DiscForge', 'dforge' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($Root, [System.StringComparison]::OrdinalIgnoreCase) })
    if ($procs.Count -eq 0) { Write-Ok 'No DiscForge running from this folder.'; return }
    foreach ($p in $procs) { Write-Info "Closing $($p.Name) (pid $($p.Id)) - $($p.Path)" }
    $procs | Stop-Process -Force
    Start-Sleep -Milliseconds 500   # let Windows release the file handles
    Write-Ok "Closed $($procs.Count) running instance(s)."
}

function Get-FolderSizeMB([string]$Path) {
    if (-not (Test-Path $Path)) { return 0 }
    $sum = (Get-ChildItem $Path -Recurse -File | Measure-Object Length -Sum).Sum
    if (-not $sum) { return 0 }
    return [math]::Round($sum / 1MB, 1)
}

# ---------------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------------

# Find the repo: the given/script folder first, then the current folder, then the usual
# C:\dev\DiscForge, and finally ask with a folder picker - so a copy of this script run from
# somewhere else (Desktop, Downloads) still finds the code.
function Test-RepoRoot([string]$Path) { return $Path -and (Test-Path (Join-Path $Path 'DiscForge.sln')) }
$candidates = @($Repo, (Get-Location).Path, 'C:\dev\DiscForge')
$found = $candidates | Where-Object { Test-RepoRoot $_ } | Select-Object -First 1
if (-not $found) {
    Write-Host "DiscForge.sln isn't in '$Repo' - please pick your DiscForge folder..." -ForegroundColor Yellow
    try {
        Add-Type -AssemblyName System.Windows.Forms | Out-Null
        $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
        $dlg.Description = 'Select your DiscForge folder (the one containing DiscForge.sln)'
        if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK -and (Test-RepoRoot $dlg.SelectedPath)) {
            $found = $dlg.SelectedPath
        }
    } catch { }
}
if (-not $found) {
    Write-Host "BUILD FAILED: couldn't find DiscForge.sln. Run the copy of this script inside your DiscForge" -ForegroundColor Red
    Write-Host "folder (C:\dev\DiscForge\build-discforge.bat), or pass -Repo C:\path\to\DiscForge." -ForegroundColor Red
    if (-not $NoPause) { [void](Read-Host 'Press Enter to close') }
    exit 1
}
$Repo = (Resolve-Path $found).Path
$logDir = Join-Path $Repo 'build-logs'
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$logFile = Join-Path $logDir ('build-{0:yyyyMMdd-HHmmss}.txt' -f $script:StartTime)
$transcribing = $false
try { Start-Transcript -Path $logFile -Force | Out-Null; $transcribing = $true } catch { }

$appExe = $null
$setupExe = $null

try {
    Write-Host ''
    Write-Host 'DiscForge - full build (app + installer)' -ForegroundColor White
    Write-Host "Repo          : $Repo"
    Write-Host "Configuration : $Configuration"
    Write-Host "Log           : $logFile"

    # --- 1. Checks ---------------------------------------------------------------------------
    Write-Step '1/7  Checking tools'

    $sln = Join-Path $Repo 'DiscForge.sln'
    if (-not (Test-Path $sln)) {
        Stop-Build "DiscForge.sln not found in '$Repo'. Put this script in the repo root, or pass -Repo C:\path\to\DiscForge."
    }
    Write-Ok 'Found DiscForge.sln'

    $dotnet = Get-Command 'dotnet' -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        Stop-Build '.NET SDK not found. Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0 and open a NEW PowerShell window.'
    }
    $sdks = @(& dotnet --list-sdks 2>$null)
    $sdk8 = @($sdks | Where-Object { $_ -match '^8\.0\.' })
    if ($sdk8.Count -eq 0) {
        Stop-Build (".NET 8.0 SDK not found (global.json pins 8.0.x). Installed SDKs: " + ($(if ($sdks) { $sdks -join '; ' } else { 'none' })) +
                    ". Install it from https://dotnet.microsoft.com/download/dotnet/8.0")
    }
    Write-Ok ('.NET SDK ' + (& dotnet --version))

    $appCsproj = Join-Path $Repo 'src\DiscForge.App\DiscForge.App.csproj'
    $cliCsproj = Join-Path $Repo 'src\DiscForge.Cli\DiscForge.Cli.csproj'
    foreach ($p in @($appCsproj, $cliCsproj)) { if (-not (Test-Path $p)) { Stop-Build "Missing project: $p" } }
    $versionNode = Select-Xml -Path $appCsproj -XPath '//Version' | Select-Object -First 1
    $version = if ($versionNode) { $versionNode.Node.InnerText } else { '?' }
    Write-Ok "Building DiscForge version $version"

    $iscc = $null
    $iss = Join-Path $Repo 'installer\DiscForge.iss'
    if (-not $NoInstaller) {
        $iscc = Find-InnoSetup
        if ($iscc) { Write-Ok "Inno Setup: $iscc" }
        else {
            Write-Warn 'Inno Setup 6 not found, so the installer .exe will NOT be made (the app .exe still will).'
            Write-Info 'Install it with:  winget install JRSoftware.InnoSetup'
            Write-Info 'or from https://jrsoftware.org/isdl.php, then run this script again.'
        }
        if (-not (Test-Path $iss)) { Write-Warn "installer\DiscForge.iss not found; the installer step will be skipped."; $iscc = $null }
    }

    # --- 2. Close running copies ---------------------------------------------------------------
    Write-Step '2/7  Closing any running DiscForge from this folder'
    Stop-RunningDiscForge $Repo

    # --- 3. Build ------------------------------------------------------------------------------
    Write-Step "3/7  Building the solution ($Configuration)"
    if ($Clean) {
        if ((Invoke-Tool 'dotnet' @('clean', $sln, '-c', $Configuration, '--nologo', '-v', 'minimal')) -ne 0) {
            Stop-Build 'dotnet clean failed (see above).'
        }
    }
    if ((Invoke-Tool 'dotnet' @('restore', $sln, '--nologo')) -ne 0) {
        Stop-Build 'Package restore failed (see above). Check your internet connection / NuGet access.'
    }
    if ((Invoke-Tool 'dotnet' @('build', $sln, '-c', $Configuration, '--no-restore', '--nologo', '-v', 'minimal')) -ne 0) {
        Stop-Build 'Compile failed - the first "error CS...." line above is the one to fix.'
    }
    Write-Ok 'Solution built.'

    # --- 4. Tests ------------------------------------------------------------------------------
    Write-Step '4/7  Running tests'
    if ($SkipTests) { Write-Warn 'Tests skipped (-SkipTests).' }
    else {
        if ((Invoke-Tool 'dotnet' @('test', $sln, '-c', $Configuration, '--no-build', '--nologo', '-v', 'minimal')) -ne 0) {
            Stop-Build 'One or more tests failed (see the "Failed" lines above). Re-run with -SkipTests to build anyway.'
        }
        Write-Ok 'All tests passed.'
    }

    # --- 5. Command docs -----------------------------------------------------------------------
    Write-Step '5/7  Checking CLI command docs'
    $sync = Join-Path $Repo 'scripts\check-commands-sync.ps1'
    if (Test-Path $sync) {
        try {
            & $sync
            if ($LASTEXITCODE -ne 0) { Write-Warn 'docs\COMMANDS.md is out of step with the CLI help (not fatal).' }
        }
        catch { Write-Warn ("Command-doc check could not run: " + $_.Exception.Message) }
    }
    else { Write-Info 'scripts\check-commands-sync.ps1 not present - skipped.' }

    # --- 6. Publish ----------------------------------------------------------------------------
    Write-Step '6/7  Publishing the self-contained app to .\publish'
    $publish = Join-Path $Repo 'publish'
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    New-Item -ItemType Directory -Path $publish | Out-Null

    $common = @('-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true',
                '-p:PublishSingleFile=false', '-p:DebugType=none', '-p:DebugSymbols=false',
                '--nologo', '-v', 'minimal', '-o', $publish)

    if ((Invoke-Tool 'dotnet' (@('publish', $appCsproj) + $common)) -ne 0) { Stop-Build 'Publishing the GUI app failed (see above).' }
    Write-Ok 'GUI published.'
    # The CLI multi-targets; the Windows build (with the burning stack) is the one that ships.
    if ((Invoke-Tool 'dotnet' (@('publish', $cliCsproj, '-f', 'net8.0-windows') + $common)) -ne 0) { Stop-Build 'Publishing the CLI failed (see above).' }
    Write-Ok 'CLI published.'

    $license = Join-Path $Repo 'LICENSE'
    if (Test-Path $license) { Copy-Item $license (Join-Path $publish 'LICENSE.txt') -Force }
    else { Write-Warn 'LICENSE not found; the installer licence page needs publish\LICENSE.txt.' }
    $docs = Join-Path $Repo 'docs'
    if (Test-Path $docs) {
        $docsOut = Join-Path $publish 'docs'
        New-Item -ItemType Directory -Path $docsOut -Force | Out-Null
        Copy-Item (Join-Path $docs '*') $docsOut -Recurse -Force
    }

    $appExe = Join-Path $publish 'DiscForge.exe'
    $cliExe = Join-Path $publish 'dforge.exe'
    foreach ($exe in @($appExe, $cliExe)) {
        if (-not (Test-Path $exe)) { Stop-Build "Publish finished but $exe is missing." }
    }
    $fileVersion = (Get-Item $appExe).VersionInfo.FileVersion
    Write-Ok ("App: {0}  (file version {1}, publish folder {2} MB)" -f $appExe, $fileVersion, (Get-FolderSizeMB $publish))

    # --- 7. Installer --------------------------------------------------------------------------
    Write-Step '7/7  Building the installer'
    if ($NoInstaller) { Write-Info 'Skipped (-NoInstaller).' }
    elseif (-not $iscc) { Write-Warn 'Skipped - Inno Setup 6 is not installed (see step 1).' }
    else {
        $outDir = Join-Path $Repo 'installer\Output'
        $before = Get-Date
        if ((Invoke-Tool $iscc @('/Qp', $iss)) -ne 0) {
            Stop-Build 'Inno Setup could not compile installer\DiscForge.iss (see above).'
        }
        $setupExe = Get-ChildItem $outDir -Filter 'DiscForge-Setup-*.exe' -ErrorAction SilentlyContinue |
                    Where-Object { $_.LastWriteTime -ge $before.AddSeconds(-5) } |
                    Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($setupExe) {
            Write-Ok ("Installer: {0}  ({1:N1} MB)" -f $setupExe.FullName, ($setupExe.Length / 1MB))
        }
        else { Write-Warn "Inno Setup reported success but no new DiscForge-Setup-*.exe appeared in $outDir." }
    }
}
catch {
    $script:Failed = $true
    Write-Host ''
    Write-Host $_.Exception.Message -ForegroundColor Red
    if ($_.InvocationInfo -and $_.Exception.Message -notlike 'BUILD FAILED:*') {
        Write-Host $_.InvocationInfo.PositionMessage -ForegroundColor DarkRed
    }
}
finally {
    $elapsed = (Get-Date) - $script:StartTime
    Write-Host ''
    Write-Host '=================================================================' -ForegroundColor White
    if ($script:Failed) {
        Write-Host ('  BUILD FAILED after {0:mm\:ss}' -f $elapsed) -ForegroundColor Red
    }
    else {
        Write-Host ('  BUILD SUCCEEDED in {0:mm\:ss}' -f $elapsed) -ForegroundColor Green
        if ($appExe)   { Write-Host "  App (no install needed) : $appExe" -ForegroundColor Green }
        if ($setupExe) { Write-Host "  Installer               : $($setupExe.FullName)" -ForegroundColor Green }
    }
    if ($script:Warnings.Count -gt 0) {
        Write-Host "  Warnings:" -ForegroundColor Yellow
        foreach ($w in $script:Warnings) { Write-Host "    - $w" -ForegroundColor Yellow }
    }
    Write-Host "  Full log                : $logFile"
    Write-Host '=================================================================' -ForegroundColor White

    if ($transcribing) { try { Stop-Transcript | Out-Null } catch { } }

    if (-not $script:Failed) {
        if ($Run -and $appExe -and (Test-Path $appExe)) {
            Write-Host 'Starting DiscForge...' -ForegroundColor Cyan
            try { Start-Process $appExe } catch { Write-Host "Could not start it: $($_.Exception.Message)" -ForegroundColor Yellow }
        }
        if (-not $NoPause) {
            $target = if ($setupExe) { $setupExe.DirectoryName } else { Join-Path $Repo 'publish' }
            if (Test-Path $target) { try { Start-Process explorer.exe $target } catch { } }
        }
    }
    if (-not $NoPause) { [void](Read-Host 'Press Enter to close') }
}

if ($script:Failed) { exit 1 } else { exit 0 }
