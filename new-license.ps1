# DiscForge - generate licence keys on this machine.
#
# ONE-TIME setup (creates your secret signing key):
#   .\new-license.ps1 -Setup
#   -> then paste the printed public key into LicenseConfig.PublicKeyBase64 and rebuild.
#
# Issue a key to a customer:
#   .\new-license.ps1 -Name "Acme Studio"
#   .\new-license.ps1 -Name "Acme Studio" -Edition Pro -Days 365
#   .\new-license.ps1 -Name "Acme" -Machine 2879-A832-3A9B-5682 -Out acme.key
#
# The private key lives in .\keys\ (git-ignored). Keep it secret and backed up:
# anyone with it can mint valid licences; if you lose it, no new keys can be made
# for the currently-shipped app.

param(
    [switch]$Setup,
    [string]$Name = "",
    [string]$Edition = "Standard",
    [int]$Days = 0,                 # 0 = perpetual
    [string]$Machine = "",          # customer's machine id -> a machine-locked key
    [string]$PrivateKey = "",       # defaults to .\keys\private.pem
    [string]$Out = ""               # optional: also save the key to this file
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }
if (-not $PrivateKey) { $PrivateKey = Join-Path $root "keys\private.pem" }
$publicKey = Join-Path (Split-Path $PrivateKey) "public.txt"

$dll = Join-Path $root "src\DiscForge.Cli\bin\Release\net8.0\dforge.dll"
if (-not (Test-Path $dll)) {
    Write-Host "dforge is not built. Run  .\build.ps1  first." -ForegroundColor Yellow
    exit 1
}

if ($Setup) {
    New-Item -ItemType Directory -Force -Path (Split-Path $PrivateKey) | Out-Null
    if (Test-Path $PrivateKey) {
        Write-Host "A signing key already exists at $PrivateKey - refusing to overwrite it." -ForegroundColor Red
        Write-Host "Delete it deliberately if you really want a new one (this invalidates every key you have issued)." -ForegroundColor Yellow
        exit 1
    }
    & dotnet $dll license keygen $PrivateKey $publicKey
    if ($LASTEXITCODE -ne 0) { Write-Host "keygen failed." -ForegroundColor Red; exit 1 }
    Write-Host ""
    Write-Host "NEXT STEPS:" -ForegroundColor Cyan
    Write-Host "  1. Open src\DiscForge.Core\Licensing\License.cs" -ForegroundColor Cyan
    Write-Host "  2. Replace LicenseConfig.PublicKeyBase64 with the contents of $publicKey" -ForegroundColor Cyan
    Write-Host "  3. Rebuild:  .\build.ps1" -ForegroundColor Cyan
    Write-Host "  4. Back up $PrivateKey somewhere safe and OFFLINE. Never commit or share it." -ForegroundColor Cyan
    exit 0
}

if (-not $Name) {
    Write-Host "Usage:" -ForegroundColor Yellow
    Write-Host "  .\new-license.ps1 -Setup                         (one-time: create your signing key)"
    Write-Host "  .\new-license.ps1 -Name '...' [-Edition Pro] [-Days 365] [-Machine <id>] [-Out file.key]"
    exit 1
}
if (-not (Test-Path $PrivateKey)) {
    Write-Host "No signing key at $PrivateKey." -ForegroundColor Yellow
    Write-Host "Run  .\new-license.ps1 -Setup  first (one time)." -ForegroundColor Cyan
    exit 1
}

$argl = @("license", "issue", "--private", $PrivateKey, "--name", $Name, "--edition", $Edition)
if ($Days -gt 0) { $argl += @("--days", "$Days") }
if ($Machine)    { $argl += @("--machine", $Machine) }

$output = & dotnet $dll @argl
if ($LASTEXITCODE -ne 0) { Write-Host "Issue failed." -ForegroundColor Red; exit 1 }

# The key is the last non-empty line the tool printed.
$key = ($output | Where-Object { $_ -and $_.Trim().Length -gt 0 } | Select-Object -Last 1).Trim()

$span = if ($Days -gt 0) { "$Days days" } else { "perpetual" }
$lock = if ($Machine) { ", machine $Machine" } else { "" }
Write-Host ""
Write-Host "Licence key for $Name ($Edition, $span$lock):" -ForegroundColor Green
Write-Host $key
if ($Out) {
    [System.IO.File]::WriteAllText($Out, $key)
    Write-Host "Saved to $Out" -ForegroundColor Cyan
}
