# DiscForge - build and launch the WinForms desktop app.
# Usage:
#   .\run-app.ps1            # build Release and run
#   .\run-app.ps1 Debug      # build Debug and run
#   .\run-app.ps1 -NoBuild   # skip the build, just launch the last build
param(
    [string]$Config = "Release",
    [switch]$NoBuild
)
$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

$proj = "src\DiscForge.App\DiscForge.App.csproj"
$exe  = Join-Path $PSScriptRoot "src\DiscForge.App\bin\$Config\net8.0-windows\DiscForge.exe"

if (-not $NoBuild) {
    Write-Host "Building DiscForge.App ($Config)..." -ForegroundColor Cyan
    dotnet build $proj -c $Config
    if ($LASTEXITCODE -ne 0) { Write-Host "Build failed." -ForegroundColor Red; exit 1 }
}

if (-not (Test-Path $exe)) {
    Write-Host "Executable not found at $exe" -ForegroundColor Red
    Write-Host "Build it first (run without -NoBuild), or check the configuration name." -ForegroundColor Yellow
    exit 1
}

Write-Host "Launching $exe" -ForegroundColor Green
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
