# DiscForge — build the GUI app in Release and launch it.
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File .\build-and-run.ps1
# Options:
#   -Configuration Debug     build Debug instead of Release
#   -Cli                     also build the dforge command-line tool
#   -NoLaunch                build only, don't start the app

param(
    [string]$Configuration = "Release",
    [switch]$Cli,
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $root

Write-Host "DiscForge build ($Configuration)" -ForegroundColor Cyan
Write-Host "  repo: $root"

# The GUI app (WinForms) pulls in Core + Devices transitively.
$appProj = Join-Path $root "src\DiscForge.App\DiscForge.App.csproj"
dotnet build $appProj -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "GUI build failed." }

# Optionally build the CLI (dforge.exe) too.
if ($Cli) {
    $cliProj = Join-Path $root "src\DiscForge.Cli\DiscForge.Cli.csproj"
    dotnet build $cliProj -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "CLI build failed." }
    $dforge = Join-Path $root "src\DiscForge.Cli\bin\$Configuration\net8.0\dforge.exe"
    Write-Host "  CLI:  $dforge" -ForegroundColor DarkGray
}

$exe = Join-Path $root "src\DiscForge.App\bin\$Configuration\net8.0-windows\DiscForge.exe"
if (-not (Test-Path $exe)) { throw "Built app not found at: $exe" }

$ver = (Get-Item $exe).VersionInfo.ProductVersion
Write-Host "Built DiscForge $ver" -ForegroundColor Green
Write-Host "  app:  $exe" -ForegroundColor DarkGray

if ($NoLaunch) {
    Write-Host "Skipping launch (-NoLaunch)." -ForegroundColor Yellow
} else {
    Write-Host "Launching..." -ForegroundColor Green
    Start-Process -FilePath $exe
}
