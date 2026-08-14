# DiscForge — burn-day fixture setup. Run once from an empty working folder.
# Creates the payload bins and every burn-day CUE (rungs 3-7 of docs/RAW_DAO.md).
# Then, for each disc, with a blank CD-R in D::
#   dotnet <dforge.dll> build-raw <cue> golden.img --subcode raw
#   dotnet <dforge.dll> inspect-raw golden.img --deep
#   dotnet <dforge.dll> burn-raw <cue> D: --engine spti
#   dotnet <dforge.dll> read-raw D: readback.bin --length <program-sectors>
#   dotnet <dforge.dll> raw-verify-readback golden.img readback.bin --report cert.html

$ErrorActionPreference = "Stop"

# --- payload bins ---------------------------------------------------------
[IO.File]::WriteAllBytes("$PWD\a.bin", (New-Object byte[] (2352*500)))   # 500-sector silence
[IO.File]::WriteAllBytes("$PWD\b.bin", (New-Object byte[] (2352*400)))   # 400-sector silence
$d = New-Object byte[] (2048*300)
for ($i = 0; $i -lt $d.Length; $i++) { $d[$i] = ($i * 7) -band 0xFF }     # deterministic data
[IO.File]::WriteAllBytes("$PWD\data.bin", $d)

# --- cues -----------------------------------------------------------------
# Rung 3 — gapless audio (program-sectors 900)
@'
FILE "a.bin" BINARY
  TRACK 01 AUDIO
    INDEX 01 00:00:00
FILE "b.bin" BINARY
  TRACK 02 AUDIO
    INDEX 01 00:00:00
'@ | Set-Content -Encoding ASCII "$PWD\gapless.cue"

# Rung 4/5 — CD-TEXT + ISRC + MCN (program-sectors 1050)
@'
CATALOG 1234567890123
TITLE "DiscForge Test Album"
PERFORMER "MaTRIX TeAm"
FILE "a.bin" BINARY
  TRACK 01 AUDIO
    TITLE "Track One"
    PERFORMER "Artist A"
    ISRC ABCDE1234567
    INDEX 01 00:00:00
FILE "b.bin" BINARY
  TRACK 02 AUDIO
    TITLE "Track Two"
    PERFORMER "Artist B"
    ISRC ABCDE7654321
    INDEX 00 00:00:00
    INDEX 01 00:02:00
'@ | Set-Content -Encoding ASCII "$PWD\meta.cue"

# Rung 6 — data Mode-1 (program-sectors 300; read --length 300)
@'
FILE "data.bin" BINARY
  TRACK 01 MODE1/2048
    INDEX 01 00:00:00
'@ | Set-Content -Encoding ASCII "$PWD\data.cue"

# Rung 7 — mixed-mode (program-sectors 550)
@'
FILE "data.bin" BINARY
  TRACK 01 MODE1/2048
    INDEX 01 00:00:00
FILE "a.bin" BINARY
  TRACK 02 AUDIO
    INDEX 00 00:00:00
    INDEX 01 00:02:00
'@ | Set-Content -Encoding ASCII "$PWD\mixed.cue"

Write-Host "Fixtures ready: a.bin b.bin data.bin + gapless.cue meta.cue data.cue mixed.cue"
Write-Host "Next: build-raw <cue> golden.img --subcode raw  →  inspect-raw  →  burn-raw  →  read-raw  →  raw-verify-readback"
