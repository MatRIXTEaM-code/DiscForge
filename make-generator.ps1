# DiscForge - build the standalone License Generator into ONE self-contained .exe.
#
# Usage (from C:\dev\DiscForge):  .\make-generator.ps1
# Output:  .\dist\generator\DiscForgeLicenseGen.exe   (copy it anywhere; no .NET needed)
#
# This is a VENDOR tool. Do not ship it with the product, and keep your private key
# (and this generator) to yourself.

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }
$proj = Join-Path $root "tools\LicenseGen\LicenseGen.csproj"
$out  = Join-Path $root "dist\generator"

Write-Host "Publishing the License Generator -> $out ($Configuration / $Runtime)" -ForegroundColor Cyan
dotnet publish $proj -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:DebugSymbols=false -o $out
if ($LASTEXITCODE -ne 0) { Write-Host "Publish failed." -ForegroundColor Red; exit 1 }

$exe = Join-Path $out "DiscForgeLicenseGen.exe"
if (Test-Path $exe) {
    Write-Host ""
    Write-Host "Done -> $exe" -ForegroundColor Green
    Write-Host "Copy that single .exe wherever you like and double-click it to issue keys." -ForegroundColor Cyan
    Write-Host "(To try it without publishing:  dotnet run --project tools\LicenseGen )" -ForegroundColor DarkGray
} else {
    Write-Host "Build reported success but the exe is missing at $exe" -ForegroundColor Yellow
}
