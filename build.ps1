# DiscForge - full local build (Core, Devices, CLI, and the WinForms GUI) + optional
# standalone publish + tests. The runnable build is produced before tests, so a test
# hiccup never blocks it; by default tests are best-effort and don't fail the script
# (use -StrictTest to make them fail it).
#
# Usage (PowerShell, from C:\dev\DiscForge - note the .\):
#   .\build.ps1                     build + tests
#   .\build.ps1 -Rebuild            clean rebuild (-t:Rebuild) + tests
#   .\build.ps1 -Publish            build + standalone .exe's in dist\ + tests
#   .\build.ps1 -Publish -NoTest    build + standalone .exe's, skip tests
#   .\build.ps1 -StrictTest         build + tests, and FAIL the script if any test fails
#   .\build.ps1 -Docs               regenerate docs\GUI.md (from HelpContent.cs) first
#   .\build.ps1 -Run                build, then launch the GUI (elevated)
#
# If PowerShell blocks this ("running scripts is disabled"), run it once as:
#   powershell -ExecutionPolicy Bypass -File .\build.ps1
# or allow scripts for your user: Set-ExecutionPolicy -Scope CurrentUser RemoteSigned

param(
    [string]$Configuration = "Release",
    [switch]$Rebuild,
    [switch]$NoTest,
    [switch]$StrictTest,
    [switch]$Docs,
    [switch]$Publish,
    [switch]$Run
)

$root = $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }
$sln = Join-Path $root "DiscForge.sln"
$started = Get-Date

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "The .NET SDK ('dotnet') was not found on PATH. Install the .NET 8 SDK." -ForegroundColor Yellow
    exit 1
}

Write-Host ""
Write-Host "== Toolchain ==" -ForegroundColor Cyan
dotnet --version

# Optionally regenerate the GUI doc from the in-app help. Non-fatal: a doc hiccup
# never blocks the build. Uses the PowerShell generator (no Python needed).
if ($Docs) {
    Write-Host ""
    Write-Host "== Regenerating docs\GUI.md ==" -ForegroundColor Cyan
    $gen = Join-Path $root "scripts\gen_gui_doc.ps1"
    if (Test-Path $gen) {
        try { & $gen } catch { Write-Host "Doc regeneration failed (skipping): $_" -ForegroundColor Yellow }
    } else {
        Write-Host "scripts\gen_gui_doc.ps1 not found - skipping." -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "== Building the solution ($Configuration) ==" -ForegroundColor Cyan
$buildArgs = @("build", $sln, "-c", $Configuration, "--nologo")
if ($Rebuild) { $buildArgs += "-t:Rebuild" }
dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { Write-Host "Build failed." -ForegroundColor Red; exit 1 }

if ($Publish) {
    $cliOut = Join-Path $root "dist\cli"
    $guiOut = Join-Path $root "dist\gui"
    $cliProj = Join-Path $root "src\DiscForge.Cli\DiscForge.Cli.csproj"
    $guiProj = Join-Path $root "src\DiscForge.App\DiscForge.App.csproj"

    Write-Host ""
    Write-Host "== Publishing standalone CLI ==" -ForegroundColor Cyan
    dotnet publish $cliProj -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $cliOut --nologo
    if ($LASTEXITCODE -ne 0) { Write-Host "CLI publish failed." -ForegroundColor Red; exit 1 }

    Write-Host ""
    Write-Host "== Publishing standalone GUI ==" -ForegroundColor Cyan
    dotnet publish $guiProj -c $Configuration -r win-x64 --self-contained true -o $guiOut --nologo
    if ($LASTEXITCODE -ne 0) { Write-Host "GUI publish failed." -ForegroundColor Red; exit 1 }

    Write-Host ""
    Write-Host ("Standalone GUI: " + (Join-Path $guiOut "DiscForge.exe") + "   (run as administrator)") -ForegroundColor Green
    Write-Host ("Standalone CLI: " + (Join-Path $cliOut "dforge.exe")) -ForegroundColor Green
}

$testsFailed = $false
if (-not $NoTest) {
    Write-Host ""
    Write-Host "== Tests ==" -ForegroundColor Cyan
    $harness = Join-Path $root "tests\Harness\Harness.csproj"
    if (Test-Path $harness) {
        # NOTE: no --nologo here - dotnet run forwards it to the harness as a test
        # filter, which would silently match zero tests.
        dotnet run --project $harness -c $Configuration
        if ($LASTEXITCODE -ne 0) {
            $testsFailed = $true
            if ($StrictTest) { Write-Host "Tests failed." -ForegroundColor Red; exit 1 }
            Write-Host "Tests reported failures - the build/publish above still succeeded." -ForegroundColor Yellow
        }
    } else {
        Write-Host "Test runner (tests\Harness) not present locally - skipping." -ForegroundColor Yellow
    }
}

$elapsed = [int]((Get-Date) - $started).TotalSeconds
Write-Host ""
Write-Host "== Done in ${elapsed}s ==" -ForegroundColor Green
$guiExe = Get-ChildItem (Join-Path $root "src\DiscForge.App\bin\$Configuration") -Filter DiscForge.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($guiExe) { Write-Host ("Framework GUI: " + $guiExe.FullName + "   (run as administrator)") -ForegroundColor Green }

if ($Run -and $guiExe) {
    Write-Host "Launching the GUI (elevated)..." -ForegroundColor Cyan
    Start-Process -FilePath $guiExe.FullName -Verb RunAs
}
