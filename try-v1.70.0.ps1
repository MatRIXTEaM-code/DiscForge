# DiscForge v1.70.0 — rebuild + a guided tour of everything new this session:
# the GameCube preservation backlog (ring codes, save banners, boot-chain confirmation,
# CRC32-confirmed junk reconstruction, revision-aware DAT tags) and the new offline
# burn-plan write-knob previewer. Paste this whole block into a PowerShell window opened
# at C:\dev\DiscForge (or run:  .\try-v1.70.0.ps1  from that folder).

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }
Set-Location $root

Write-Host ""
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host " DiscForge v1.70.0 - rebuild + new-feature tour" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan

# ---- 1. Rebuild (clean, so nothing stale is being tested) -----------------
Write-Host ""
Write-Host "== Rebuilding (this can take a minute) ==" -ForegroundColor Cyan
.\build.ps1 -Rebuild
if ($LASTEXITCODE -ne 0) { Write-Host "Build failed - stopping here." -ForegroundColor Red; exit 1 }

# ---- 2. Find the freshly-built CLI -----------------------------------------
$cli = Get-ChildItem (Join-Path $root "src\DiscForge.Cli\bin\Release") -Filter dforge.exe -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $cli) { Write-Host "Could not find dforge.exe after building." -ForegroundColor Red; exit 1 }
Write-Host ""
Write-Host "Using: $cli" -ForegroundColor DarkGray

function Demo($title, [scriptblock]$cmd) {
    Write-Host ""
    Write-Host "---- $title ----" -ForegroundColor Yellow
    & $cmd
}

# ---- 3. Version check -------------------------------------------------------
Demo "Version" { & $cli version }

# ---- 4. Burn-plan: offline write-knob preview (no drive, no disc needed) --
Demo "burn-plan: RAW DAO-96, BURN-Proof on, link size 7, 8x" {
    & $cli burn-plan --write-type raw --burn-proof --link-size 7 --speed 8
}
Demo "burn-plan: Session-At-Once, test-write (laser off), as JSON" {
    & $cli burn-plan --write-type sao --test-write --json
}

# ---- 5. GameCube ring codes: red/blue/green as plain text, no file needed -
Demo "gc-ringcode: a clean match against a disc header's own game code" {
    & $cli gc-ringcode "C03B2606" "DOL-GALE-0-00 USA" "S0" --game-code GALE
}
Demo "gc-ringcode: a deliberate mismatch, to see the flag fire" {
    & $cli gc-ringcode "-" "DOL-GALE-0-00 USA" "S1" --game-code GALJ
}

# ---- 6. DAT name-tag parsing: also just plain text, no DAT file needed ----
Demo "dat-tags: a normal revision" {
    & $cli dat-tags "Super Smash Bros. Melee (USA) (Rev 2)"
}
Demo "dat-tags: a demo disc" {
    & $cli dat-tags "Interactive Multi-Game Demo Disc (USA) (Demo) (v35)"
}

# ---- 7. Commands that need a real file - shown as usage/help only ---------
Write-Host ""
Write-Host "---- The following need a real file to try against - showing usage only ----" -ForegroundColor Yellow
Demo "gc-verify --help (full single-image GameCube health check)" { & $cli gc-verify }
Demo "gci-banner --help (decode a save's own banner/icon to PNG)" { & $cli gci-banner }
Demo "gc-junk-fill --help (padding reconstruction, --expect-crc32 confirms against Redump)" { & $cli gc-junk-fill }

Write-Host ""
Write-Host "=====================================================" -ForegroundColor Green
Write-Host " Done. To try the file-based commands for real:" -ForegroundColor Green
Write-Host "   $cli gc-verify <your.iso> --json" -ForegroundColor Green
Write-Host "   $cli gci-banner <your.gci> <out-folder>" -ForegroundColor Green
Write-Host "=====================================================" -ForegroundColor Green
