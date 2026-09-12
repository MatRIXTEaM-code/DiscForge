#Requires -Version 5.1
<#
    build-app.ps1 - build the latest DiscForge app from a dev folder you choose.

    Usage:
        .\build-app.ps1                       # pops a folder picker to choose the repo
        .\build-app.ps1 -Repo C:\dev\DiscForge
        .\build-app.ps1 -Test                 # also run the xUnit test suite
        .\build-app.ps1 -Publish              # also publish + compile installer\Output\DiscForge-Setup-<version>.exe
                                               # (the compile step needs Inno Setup 6 — see installer\README.md)
        .\build-app.ps1 -Run                  # launch the GUI after building

    Requirements: .NET 8 SDK (`dotnet --version` >= 8) on Windows. The GUI (DiscForge.App)
    and the burning CLI (net8.0-windows) build on Windows only.
#>
[CmdletBinding()]
param(
    [string]$Repo,
    [switch]$Test,
    [switch]$Publish,
    [switch]$Run,
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'

function Fail($m) { Write-Host "ERROR: $m" -ForegroundColor Red; exit 1 }

# 1) Choose the dev folder (dialog if not supplied).
if (-not $Repo) {
    Add-Type -AssemblyName System.Windows.Forms | Out-Null
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Select your DiscForge dev folder (the one containing DiscForge.sln)'
    $dlg.SelectedPath = 'C:\dev\DiscForge'
    if ($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { Fail 'No folder selected.' }
    $Repo = $dlg.SelectedPath
}
if (-not (Test-Path (Join-Path $Repo 'DiscForge.sln'))) { Fail "No DiscForge.sln in '$Repo'." }
Set-Location $Repo
Write-Host "DiscForge dev folder: $Repo" -ForegroundColor Cyan

# 2) Tooling check.
try { $sdk = (& dotnet --version).Trim() } catch { Fail 'The .NET SDK (`dotnet`) was not found on PATH.' }
if ([version]($sdk -split '-')[0] -lt [version]'8.0') { Fail ".NET 8 SDK required; found $sdk." }
Write-Host ".NET SDK: $sdk" -ForegroundColor DarkGray

# 3) Report the version being built.
$appCsproj = Join-Path $Repo 'src\DiscForge.App\DiscForge.App.csproj'
$ver = ([xml](Get-Content $appCsproj)).Project.PropertyGroup.Version
Write-Host "Building DiscForge app version $ver ($Configuration)..." -ForegroundColor Yellow

# 3b) Close any DiscForge.exe left running from a previous -Run. Each -Run launches a
# detached process and never closes it, so re-building without closing that window first
# fails with MSB3027/MSB3021 ("file is locked by: DiscForge (<pid>)") when the App build
# tries to overwrite its own copy of DiscForge.Core.dll/DiscForge.Devices.dll. Matched by
# full path (not just process name) so this can never touch an unrelated program.
$appExePreBuild = Join-Path $Repo "src\DiscForge.App\bin\$Configuration\net8.0-windows\DiscForge.exe"
$running = @(Get-Process -Name 'DiscForge' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and ($_.Path -ieq $appExePreBuild) })
if ($running.Count -gt 0) {
    Write-Host "`nClosing $($running.Count) running DiscForge.exe instance(s) from a previous -Run..." -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 300   # give the OS a moment to release the file handles
}

# 4) Build the whole solution (Core, CLI [net8.0 + net8.0-windows], App, tests).
& dotnet build (Join-Path $Repo 'DiscForge.sln') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { Fail 'Build failed.' }

# 5) Optional: run the test suite.
if ($Test) {
    Write-Host "`nRunning tests..." -ForegroundColor Yellow
    & dotnet test (Join-Path $Repo 'DiscForge.sln') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { Fail 'Tests failed.' }
}

# 6) Optional: keep the command reference honest.
$sync = Join-Path $Repo 'scripts\check-commands-sync.ps1'
if (Test-Path $sync) { Write-Host "`nChecking command docs are in sync..." -ForegroundColor Yellow; & $sync }

# 7) Optional: self-contained installer payload, then package it into the actual installer
# .exe. publish.ps1 alone only produces the self-contained folder at .\publish\ — the real
# installer.exe (installer\Output\DiscForge-Setup-<version>.exe) needs Inno Setup's compiler,
# ISCC.exe, run against installer\DiscForge.iss. This script never did that step before, which
# is why -Publish always left installer\Output\ empty even when it otherwise succeeded.
if ($Publish) {
    $pub = Join-Path $Repo 'installer\publish.ps1'
    if (Test-Path $pub) {
        Write-Host "`nProducing self-contained payload..." -ForegroundColor Yellow
        & $pub
        if ($LASTEXITCODE -ne 0) { Fail 'Publish failed.' }

        $issScript = Join-Path $Repo 'installer\DiscForge.iss'
        if (Test-Path $issScript) {
            # Find ISCC.exe: PATH first, then the two locations the Inno Setup 6 installer
            # normally uses (32-bit and 64-bit Program Files).
            $isccPath = $null
            $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
            if ($onPath) { $isccPath = $onPath.Source }
            else {
                $candidates = @(
                    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
                    'C:\Program Files\Inno Setup 6\ISCC.exe'
                )
                $isccPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
            }

            if ($isccPath) {
                Write-Host "`nCompiling the installer (Inno Setup)..." -ForegroundColor Yellow
                & $isccPath $issScript
                if ($LASTEXITCODE -ne 0) { Fail 'Installer compile failed.' }
                $outDir = Join-Path $Repo 'installer\Output'
                $setupExe = Get-ChildItem $outDir -Filter 'DiscForge-Setup-*.exe' -ErrorAction SilentlyContinue |
                            Sort-Object LastWriteTime -Descending | Select-Object -First 1
                if ($setupExe) { Write-Host "  Installer: $($setupExe.FullName)" -ForegroundColor Green }
                else { Write-Host "  ISCC reported success but no DiscForge-Setup-*.exe was found in $outDir." -ForegroundColor DarkYellow }
            }
            else {
                Write-Host "  Inno Setup 6 (ISCC.exe) was not found on PATH or in its usual install " -ForegroundColor DarkYellow -NoNewline
                Write-Host "location. The self-contained payload is in .\publish\, but the packaged " -ForegroundColor DarkYellow -NoNewline
                Write-Host "installer .exe was not produced. Install Inno Setup 6 " -ForegroundColor DarkYellow -NoNewline
                Write-Host "(https://jrsoftware.org/isinfo.php) to get installer\Output\DiscForge-Setup-<version>.exe " -ForegroundColor DarkYellow -NoNewline
                Write-Host "automatically next time." -ForegroundColor DarkYellow
            }
        }
        else { Write-Host "  installer\DiscForge.iss not found; skipping installer compile." -ForegroundColor DarkYellow }
    }
    else { Write-Host 'installer\publish.ps1 not found; skipping publish.' -ForegroundColor DarkYellow }
}

# 8) Report where the app landed, and optionally launch it.
# The assembly is named "DiscForge" (see <AssemblyName> in DiscForge.App.csproj), not
# "DiscForge.App" - the exe on disk is DiscForge.exe. This path used to say DiscForge.App.exe,
# which never existed, so -Run always silently failed to launch anything.
$appExe = Join-Path $Repo "src\DiscForge.App\bin\$Configuration\net8.0-windows\DiscForge.exe"
$cliDll = Join-Path $Repo "src\DiscForge.Cli\bin\$Configuration\net8.0-windows\dforge.dll"
Write-Host "`nBuild complete." -ForegroundColor Green
Write-Host "  GUI app : $appExe"
Write-Host "  CLI     : $cliDll  (run with: dotnet `"$cliDll`" <command>)"
if ($Run) {
    if (Test-Path $appExe) { Write-Host "`nLaunching the app..." -ForegroundColor Cyan; Start-Process $appExe }
    else { Write-Host 'App exe not found to launch.' -ForegroundColor DarkYellow }
}
