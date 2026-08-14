#Requires -Version 5.1
<#
    build-app.ps1 — build the latest DiscForge app from a dev folder you choose.

    Usage:
        .\build-app.ps1                       # pops a folder picker to choose the repo
        .\build-app.ps1 -Repo C:\dev\DiscForge
        .\build-app.ps1 -Test                 # also run the xUnit test suite
        .\build-app.ps1 -Publish              # also produce the self-contained installer payload
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

# 7) Optional: self-contained installer payload.
if ($Publish) {
    $pub = Join-Path $Repo 'installer\publish.ps1'
    if (Test-Path $pub) { Write-Host "`nProducing self-contained payload..." -ForegroundColor Yellow; & $pub }
    else { Write-Host 'installer\publish.ps1 not found; skipping publish.' -ForegroundColor DarkYellow }
}

# 8) Report where the app landed, and optionally launch it.
$appExe = Join-Path $Repo "src\DiscForge.App\bin\$Configuration\net8.0-windows\DiscForge.App.exe"
$cliDll = Join-Path $Repo "src\DiscForge.Cli\bin\$Configuration\net8.0-windows\dforge.dll"
Write-Host "`nBuild complete." -ForegroundColor Green
Write-Host "  GUI app : $appExe"
Write-Host "  CLI     : $cliDll  (run with: dotnet `"$cliDll`" <command>)"
if ($Run) {
    if (Test-Path $appExe) { Write-Host "`nLaunching the app..." -ForegroundColor Cyan; Start-Process $appExe }
    else { Write-Host 'App exe not found to launch.' -ForegroundColor DarkYellow }
}
