# DiscForge - publish the dforge CLI and put it on your PATH.
#
# Builds src\DiscForge.Cli into a folder of your choosing (default C:\tools\dforge),
# then adds that folder to your USER PATH so `dforge` works from any window.
#
# Usage (from C:\dev\DiscForge):
#   .\install-cli.ps1                       # publish to C:\tools\dforge, add to PATH
#   .\install-cli.ps1 -Dest D:\bin\dforge   # publish somewhere else
#   .\install-cli.ps1 -NoPath               # publish only, don't touch PATH

param(
    [string] $Dest = 'C:\tools\dforge',
    [switch] $NoPath
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }
$proj = Join-Path $root 'src\DiscForge.Cli\DiscForge.Cli.csproj'

if (-not (Test-Path $proj)) {
    Write-Host "Can't find $proj - run this from C:\dev\DiscForge." -ForegroundColor Red
    exit 1
}

Write-Host "Publishing dforge -> $Dest" -ForegroundColor Cyan
dotnet publish $proj -c Release -o $Dest
if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed (exit $LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}

$exe = Join-Path $Dest 'DiscForge.Cli.exe'
$dforge = Join-Path $Dest 'dforge.exe'
if ((Test-Path $exe) -and -not (Test-Path $dforge)) {
    # publish emits DiscForge.Cli.exe; make a dforge.exe alongside it so the command reads naturally.
    Copy-Item $exe $dforge -Force
    Write-Host "Created dforge.exe alongside DiscForge.Cli.exe" -ForegroundColor DarkGray
}

if ($NoPath) {
    Write-Host ""
    Write-Host "Done. CLI is in $Dest (PATH not modified - run it with the full path)." -ForegroundColor Green
    exit 0
}

# Add $Dest to the USER PATH exactly once.
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$parts = @()
if ($userPath) { $parts = $userPath -split ';' | Where-Object { $_ -ne '' } }
$already = $parts | Where-Object { $_.TrimEnd('\') -ieq $Dest.TrimEnd('\') }

if ($already) {
    Write-Host ""
    Write-Host "$Dest is already on your PATH - nothing to change." -ForegroundColor Green
} else {
    $newPath = ($parts + $Dest) -join ';'
    [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
    $env:Path = $env:Path + ';' + $Dest   # so it works in THIS window too
    Write-Host ""
    Write-Host "Added $Dest to your USER PATH." -ForegroundColor Green
    Write-Host "Open a NEW PowerShell window for it to stick, then run:  dforge help" -ForegroundColor Cyan
}
