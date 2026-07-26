# DiscForge - proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
#
# publish.ps1 - produce self-contained win-x64 binaries ready for the installer,
# optionally obfuscated and code-signed (see docs/SECURITY.md).
#
# Run from the repo root:   .\installer\publish.ps1
#   -Obfuscate -ConfuserCli "C:\tools\ConfuserEx\Confuser.CLI.exe"   # harden the IL
#   -Sign -CertThumbprint <thumbprint>                              # Authenticode-sign
# Output:                    .\publish\   (App + CLI + docs, self-contained)
#
# Self-contained means the .NET 8 runtime is bundled - the target PC needs no
# .NET install. Both executables are published into ONE folder so the installer
# packages a single directory.

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime       = 'win-x64',
    [string]$OutDir        = "$PSScriptRoot\..\publish",
    [switch]$Obfuscate,
    [string]$ConfuserCli   = '',
    [switch]$Sign,
    [string]$CertThumbprint = '',
    [string]$TimestampUrl   = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path "$PSScriptRoot\.."

Write-Host "DiscForge publish -> $OutDir  ($Configuration / $Runtime, self-contained)" -ForegroundColor Cyan

# Clean previous output so stale files never ship.
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir | Out-Null

# Common publish switches. Self-contained single-folder (NOT single-file: the
# WinForms app + native SPTI interop are happier as a folder, and the installer
# bundles the whole folder anyway).
$common = @(
    '-c', $Configuration
    '-r', $Runtime
    '--self-contained', 'true'
    '/p:PublishSingleFile=false'
    '/p:DebugType=none'
    '/p:DebugSymbols=false'
)

# 1) The GUI app (net8.0-windows) - this is the primary payload; publishing it
#    brings DiscForge.Core.dll and DiscForge.Devices.dll with it.
Write-Host "`nPublishing DiscForge (GUI)..." -ForegroundColor Yellow
dotnet publish "$repo\src\DiscForge.App\DiscForge.App.csproj" @common -o $OutDir
if ($LASTEXITCODE -ne 0) { throw "GUI publish failed." }

# 2) The CLI (net8.0). It shares Core with the app; publishing into the same
#    folder reuses the already-present Core/runtime assemblies and just adds
#    dforge.exe (+ its deps json).
Write-Host "`nPublishing dforge (CLI)..." -ForegroundColor Yellow
dotnet publish "$repo\src\DiscForge.Cli\DiscForge.Cli.csproj" @common -o $OutDir
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed." }

# 3) Bring the docs and licence along for the Start-menu 'Documentation' link
#    and the installer's licence page.
Write-Host "`nStaging docs + licence..." -ForegroundColor Yellow
Copy-Item "$repo\LICENSE" "$OutDir\LICENSE.txt" -Force
$docsOut = Join-Path $OutDir 'docs'
New-Item -ItemType Directory -Path $docsOut -Force | Out-Null
Copy-Item "$repo\docs\*" $docsOut -Recurse -Force

# 4) Sanity: the two executables must exist.
$app = Join-Path $OutDir 'DiscForge.exe'
$cli = Join-Path $OutDir 'dforge.exe'
foreach ($exe in @($app, $cli)) {
    if (-not (Test-Path $exe)) { throw "Expected output missing: $exe" }
}

# 5) Optional: obfuscate DiscForge's own managed assemblies (not the .NET runtime
#    DLLs) with ConfuserEx. Raises the cost of decompiling; see docs/SECURITY.md.
$ourAssemblies = @('DiscForge.dll', 'DiscForge.Core.dll', 'DiscForge.Devices.dll', 'dforge.dll')
if ($Obfuscate) {
    Write-Host "`nObfuscating (ConfuserEx)..." -ForegroundColor Yellow
    if (-not $ConfuserCli -or -not (Test-Path $ConfuserCli)) {
        Write-Host "  -ConfuserCli was not a valid path to Confuser.CLI.exe - skipping obfuscation." -ForegroundColor Red
    } else {
        $modules = ($ourAssemblies | Where-Object { Test-Path (Join-Path $OutDir $_) } |
                    ForEach-Object { '  <module path="' + $_ + '" />' }) -join "`n"
        $crproj = @(
            '<project outputDir="' + $OutDir + '" baseDir="' + $OutDir + '" xmlns="http://confuser.codeplex.com">'
            '  <rule pattern="true" preset="normal" inherit="false">'
            '    <protection id="anti tamper" />'
            '    <protection id="constants" />'
            '    <protection id="ctrl flow" />'
            '    <protection id="rename" />'
            '  </rule>'
            $modules
            '</project>'
        ) -join "`n"
        $crprojPath = Join-Path $OutDir 'confuse.crproj'
        [System.IO.File]::WriteAllText($crprojPath, $crproj)
        & $ConfuserCli -n $crprojPath
        if ($LASTEXITCODE -ne 0) { throw "Obfuscation failed." }
        Remove-Item $crprojPath -Force
        Write-Host "  Obfuscated: $($ourAssemblies -join ', ')" -ForegroundColor Green
    }
}

# 6) Optional: Authenticode-sign the executables (and our DLLs) so Windows can
#    verify authenticity and detect tampering. Needs a code-signing certificate.
if ($Sign) {
    Write-Host "`nSigning (signtool)..." -ForegroundColor Yellow
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool) {
        Write-Host "  signtool.exe not on PATH (install the Windows SDK) - skipping signing." -ForegroundColor Red
    } elseif (-not $CertThumbprint) {
        Write-Host "  -CertThumbprint not supplied - skipping signing." -ForegroundColor Red
    } else {
        $toSign = @('DiscForge.exe', 'dforge.exe') + $ourAssemblies |
                  ForEach-Object { Join-Path $OutDir $_ } | Where-Object { Test-Path $_ }
        & $signtool.Source sign /sha1 $CertThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $toSign
        if ($LASTEXITCODE -ne 0) { throw "Signing failed." }
        Write-Host "  Signed $($toSign.Count) file(s)." -ForegroundColor Green
    }
}

$size = (Get-ChildItem $OutDir -Recurse | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("`nPublish complete: {0}  ({1:N0} MB)" -f $OutDir, $size) -ForegroundColor Green
Write-Host "Next: compile installer\DiscForge.iss with Inno Setup (ISCC.exe installer\DiscForge.iss)."
