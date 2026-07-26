# DiscForge - regenerate docs\GUI.md from HelpContent.cs (the in-app manual), so the
# GUI tile reference stays a single source of truth. Also cross-checks the launcher
# and warns if a tile there has no help entry. No external dependencies (no Python).
#
# Usage:  .\scripts\gen_gui_doc.ps1          # writes docs\GUI.md
#         .\scripts\gen_gui_doc.ps1 -Check   # non-zero exit if docs\GUI.md is stale

param([switch]$Check)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$helpPath = Join-Path $root "src\DiscForge.App\Views\HelpContent.cs"
$launcherPath = Join-Path $root "src\DiscForge.App\CdrwinLauncher.cs"
$outPath = Join-Path $root "docs\GUI.md"

# Presentation grouping (doc-only concern), keyed by tile key, in display order.
$groups = @(
    @{ Name = "Disc imaging & burning"; Keys = @("record","copy","read","create","convert","inspect","rawlab","sectors","bincue","cue","browse","udfcreate") },
    @{ Name = "Hardware & devices (need raw access)"; Keys = @("drives","mount","recovery","quality") },
    @{ Name = "Audio"; Keys = @("accuraterip","ripaudio") },
    @{ Name = "Protection & interop"; Keys = @("protect","subcode","interop") },
    @{ Name = "DVD & video"; Keys = @("dvdshrink","dvdinfo","transcode","pack") },
    @{ Name = "Identify, verify & catalogue"; Keys = @("identify","examine","library","submit","tools") },
    @{ Name = "Console & cartridge preservation"; Keys = @("patch","dreamcast","milcd","dcid","xbox","memcard","psxasset","psxbuild","compimg","scummvm") },
    @{ Name = "Extract, cheats & game media"; Keys = @("extract","cheat","media") },
    @{ Name = "Collection & front-end"; Keys = @("playlists","sets") },
    @{ Name = "Utility"; Keys = @("help","settings","about","exit") }
)

$helpText = [System.IO.File]::ReadAllText($helpPath)
$launcherText = [System.IO.File]::ReadAllText($launcherPath)

# Parse HelpContent: split on `new(`, extract the quoted literals in each chunk.
$rx = [regex]'"((?:[^"\\]|\\.)*)"'
$byKey = [ordered]@{}
$order = New-Object System.Collections.Generic.List[string]
$chunks = $helpText -split 'new\('
for ($i = 1; $i -lt $chunks.Count; $i++) {
    $chunk = ($chunks[$i] -split '\};')[0]
    $lits = @($rx.Matches($chunk) | ForEach-Object { $_.Groups[1].Value })
    if ($lits.Count -lt 5) { continue }
    $body = -join $lits[4..($lits.Count - 1)]
    $byKey[$lits[0]] = [pscustomobject]@{ Key=$lits[0]; Glyph=$lits[1]; Title=$lits[2]; Summary=$lits[3]; Body=$body }
    $order.Add($lits[0])
}
$total = $order.Count

# Drift check: every launcher tile should have a help entry.
$launcherKeys = @([regex]::Matches($launcherText, 'new\("([a-z0-9]+)",') | ForEach-Object { $_.Groups[1].Value })
$missing = @($launcherKeys | Where-Object { -not $byKey.Contains($_) })
if ($missing.Count -gt 0) { Write-Host "WARNING: launcher tiles with no HelpContent entry: $($missing -join ', ')" -ForegroundColor Yellow }

$grouped = @{}
foreach ($g in $groups) { foreach ($k in $g.Keys) { $grouped[$k] = $true } }

# --- assemble the document ---
$header = @'
<!-- GENERATED FILE - do not edit by hand.
     Regenerate with:  .\scripts\gen_gui_doc.ps1   (or python3 scripts/gen_gui_doc.py)
     Source of truth:  src/DiscForge.App/Views/HelpContent.cs -->

# DiscForge - the WinForms application

`DiscForge.App` is the standalone Windows GUI. It is a thin shell over the tested
Core: the shell owns no disc logic; every view calls into `DiscForge.Core` /
`DiscForge.Devices`. The App targets `net8.0-windows` with WinForms and is
Windows-only - the Core, CLI and test harness build anywhere .NET 8 does, but the
GUI does not.

The front door is `CdrwinLauncher` - a grid of large flat icon tiles in the CDRWIN
4 idiom. Each tile opens its own task window; the hovered tile's blurb shows on the
status line. The in-app **Help** tile is the searchable version of this reference.

## Launching

On Windows, with the .NET 8 SDK:

```
dotnet run --project src/DiscForge.App
```

Single double-click executable:

```
dotnet publish src/DiscForge.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

`DiscForge.exe` then needs no .NET install. Most views run as a normal user;
**Drives**, **Record**, **Copy**, **Read**, **Mount**, **Recovery** and **Disc
Quality** need raw device access, which generally means running as administrator.

## Tiles

The launcher shows **{{COUNT}} tiles**, defined by the `_tiles` array in
`CdrwinLauncher.cs`. Each is described below; the full write-ups are in the Help
tile (source: `HelpContent.cs`).
'@

$notes = @'
## Notes

- Long-running work (create / verify / detect / burn) runs on a background thread;
  the window stays responsive and failures surface as messages, never as false success.
- The manifest requests `asInvoker` (no forced UAC) with visual styles; per-monitor
  DPI awareness is set in code.
- Views are hand-built in code (no `.Designer.cs`), so the source is fully reviewable as text.
- Diagnostics: `AppLog` writes a session log under `%APPDATA%\DiscForge\logs`; the
  About tile opens that folder. Nothing is transmitted anywhere.
'@

$lines = New-Object System.Collections.Generic.List[string]
foreach ($l in ($header -split "`n")) { $lines.Add(($l.TrimEnd("`r")).Replace("{{COUNT}}", "$total")) }
$lines.Add("")

foreach ($g in $groups) {
    $rows = @($g.Keys | Where-Object { $byKey.Contains($_) } | ForEach-Object { $byKey[$_] })
    if ($rows.Count -eq 0) { continue }
    $lines.Add("### $($g.Name)")
    $lines.Add("")
    $lines.Add("| Tile | What it does |")
    $lines.Add("|------|--------------|")
    foreach ($e in $rows) { $lines.Add("| **$($e.Title)** | $($e.Summary). |") }
    $lines.Add("")
}

# Safety net: any help entry not placed in a group.
$ungrouped = @($order | Where-Object { -not $grouped.ContainsKey($_) } | ForEach-Object { $byKey[$_] })
if ($ungrouped.Count -gt 0) {
    $lines.Add("### Other")
    $lines.Add("")
    $lines.Add("| Tile | What it does |")
    $lines.Add("|------|--------------|")
    foreach ($e in $ungrouped) { $lines.Add("| **$($e.Title)** | $($e.Summary). |") }
    $lines.Add("")
}

foreach ($l in ($notes -split "`n")) { $lines.Add($l.TrimEnd("`r")) }

$text = ($lines -join "`n").TrimEnd() + "`n"

if ($Check) {
    $current = if (Test-Path $outPath) { [System.IO.File]::ReadAllText($outPath) } else { "" }
    if ($current.TrimEnd() -ne $text.TrimEnd()) {
        Write-Host "docs\GUI.md is stale - run: .\scripts\gen_gui_doc.ps1" -ForegroundColor Yellow
        exit 1
    }
    Write-Host "docs\GUI.md is up to date."
    exit 0
}

[System.IO.File]::WriteAllText($outPath, $text, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Wrote docs\GUI.md: $total tiles."
