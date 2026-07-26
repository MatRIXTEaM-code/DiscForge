# DiscForge - build a CHD whose hunk map contains a VARIETY of hunk types.
#
# The compressed hunk map's type stream is Huffman-coded. Every CHD made from a
# small/uniform source ends up all-one-type, whose Huffman tree is degenerate and
# can't reveal the tree's bit-format. This source deliberately mixes:
#   * long runs of silence (identical hunks)  -> SELF references (type 5)
#   * incompressible random                   -> NONE (type 4)
#   * a repeating pattern                      -> zlib/LZMA (type 0/1)
#   * a tonal audio track                      -> FLAC (type 2)
# so the map carries several distinct type symbols (a rich Huffman tree) plus
# SELF refs - exactly what's needed to finish decoding the map. See docs/CHD_MAP.md.
#
# Usage (from C:\dev\DiscForge):
#   powershell -ExecutionPolicy Bypass -File .\make-mixed-chd.ps1

param([string]$Chdman = "")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if ([string]::IsNullOrEmpty($root)) { $root = (Get-Location).Path }
$samples = Join-Path $root "samples"
New-Item -ItemType Directory -Force -Path $samples | Out-Null

# locate chdman
$chd = ""
if ($Chdman -and (Test-Path $Chdman)) { $chd = (Resolve-Path $Chdman).Path }
else {
    $onPath = Get-Command chdman -ErrorAction SilentlyContinue
    if ($onPath) { $chd = $onPath.Source }
    else { foreach ($p in @((Join-Path $root "chdman.exe"), (Join-Path $root "chdman\chdman.exe"), (Join-Path $samples "chdman.exe"))) { if (Test-Path $p) { $chd = $p; break } } }
}
if ([string]::IsNullOrEmpty($chd)) { Write-Host "chdman.exe not found - put it in $root or pass -Chdman." -ForegroundColor Yellow; exit 1 }
$PSNativeCommandUseErrorActionPreference = $false
Write-Host "Using chdman: $chd"

$SECTOR = 2352
function Sectors([int]$n) { return $n * $SECTOR }

# ---- Track 1 (AUDIO): silence | random | repeating pattern ----
$sil  = New-Object byte[] (Sectors 64)                       # zeros -> identical hunks -> SELF
$rand = New-Object byte[] (Sectors 64)
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($rand)   # -> NONE
$pat  = New-Object byte[] (Sectors 64)
for ($i = 0; $i -lt $pat.Length; $i++) { $pat[$i] = [byte](($i % 37)) }          # compressible -> zlib/LZMA
$t1 = New-Object byte[] ($sil.Length + $rand.Length + $pat.Length)
[Array]::Copy($sil,  0, $t1, 0, $sil.Length)
[Array]::Copy($rand, 0, $t1, $sil.Length, $rand.Length)
[Array]::Copy($pat,  0, $t1, $sil.Length + $rand.Length, $pat.Length)

# ---- Track 2 (AUDIO): a sine tone (16-bit stereo) -> FLAC ----
$frames = 128 * ($SECTOR / 4)          # 4 bytes/frame (stereo 16-bit)
$t2 = New-Object byte[] (Sectors 128)
for ($f = 0; $f -lt $frames; $f++) {
    $s = [int](8000 * [Math]::Sin($f * 0.05))
    $lo = [byte]($s -band 0xFF); $hi = [byte](($s -shr 8) -band 0xFF)
    $o = $f * 4
    $t2[$o] = $lo; $t2[$o+1] = $hi; $t2[$o+2] = $lo; $t2[$o+3] = $hi
}

$bin1 = Join-Path $samples "mixed-t1.bin"
$bin2 = Join-Path $samples "mixed-t2.bin"
$cue  = Join-Path $samples "mixed.cue"
[System.IO.File]::WriteAllBytes($bin1, $t1)
[System.IO.File]::WriteAllBytes($bin2, $t2)
$cueText =
  "FILE `"mixed-t1.bin`" BINARY`r`n  TRACK 01 AUDIO`r`n    INDEX 01 00:00:00`r`n" +
  "FILE `"mixed-t2.bin`" BINARY`r`n  TRACK 02 AUDIO`r`n    INDEX 01 00:00:00`r`n"
[System.IO.File]::WriteAllText($cue, $cueText, [System.Text.Encoding]::ASCII)

$outChd = Join-Path $samples "test-mixed.chd"
Write-Host "Compressing to $outChd ..."
& $chd createcd -i $cue -o $outChd -f
if ($LASTEXITCODE -ne 0) { throw "chdman failed (exit $LASTEXITCODE)." }

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Get-ChildItem $samples -Filter "test-mixed.chd" | Select-Object Name, Length | Format-Table -AutoSize
Write-Host "Now tell Claude: the mixed CHD is ready."
