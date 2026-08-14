# Collection-management tools

Beyond identifying and converting individual images, DiscForge has a set of tools for
looking after a whole collection — trimming it to one copy per game, rebuilding it into
a clean canonical set, archiving it deterministically, guarding it against bit-rot, and
converting saves between the formats emulators use. All of it is cataloguing and
faithful file handling; none of it touches protection or game internals.

## 1G1R — one game, one ROM

No-Intro and Redump encode a game's region and status in its name
(`Chrono Trigger (USA) (Rev 1)`), so DiscForge can collapse every regional and revision
variant down to the single best copy without a separate metadata source. It groups a
game's variants (folding clones into their parent via the DAT's `cloneof` link, and
keeping each disc of a multi-disc game separately), then picks the keeper by your region
priority, preferring final over prototype and the highest revision.

```
dforge 1g1r <dat> [--regions USA,Europe,Japan] [--keep-proto] [--drop-unlicensed] [--out <file.dat|.txt>]
```

With `--out file.dat` it writes a filtered Logiqx DAT of just the keepers (which
round-trips back into DiscForge or any DAT tool); `--out file.txt` writes the names. The
parsing lives in `DiscForge.Core.Dat.GameName`, the selection in `OneGameOneRom`, and it
is on the **Sets** GUI tile.

## Rebuild a clean set

Point the rebuilder at a messy folder and a DAT and it verifies every file, then places
each confirmed-good one under its canonical DAT name — flat, or one folder per game for
multi-track disc sets — and reports what's still missing and what didn't match.

```
dforge rebuild <src> <dest> --dat <file> [--per-game] [--move] [--apply]
```

Without `--apply` it only prints the plan; `--move` relocates instead of copying, and a
re-run is idempotent (files already in place are skipped). Pair it with a 1G1R-filtered
DAT to rebuild only the region set you want. Also on the **Sets** tile.

## TorrentZip — deterministic archives

`torrentzip` writes a ZIP following the TorrentZip rules ROM managers rely on: entries
sorted case-insensitively, a fixed timestamp, no extra fields, and the end comment
`TORRENTZIPPED-XXXXXXXX` whose hex is the CRC-32 of the central directory. The same input
always yields byte-identical output, so a set has a stable hash.

```
dforge torrentzip <out.zip> <file> [file ...]
```

Honest caveat: byte-for-byte identity with a *different* tool's TorrentZip also needs the
identical DEFLATE encoder (classic zlib level 9). The structure, ordering, timestamps and
the TORRENTZIPPED comment here are exact and independently verifiable
(`TorrentZip.IsTorrentZipStructured`); the compressed byte stream depends on this build's
DEFLATE, which the offline build can't pin to classic zlib.

## Hash sidecars — guard against bit-rot

Generate an SFV (CRC-32), md5sum, or sha1sum sidecar for a set of files, and verify a
folder against one later.

```
dforge hashgen <sfv|md5|sha1> <out> <file> [file ...]
dforge hashverify <sidecar.sfv|.md5|.sha1>        # re-hashes and reports OK / FAIL / MISSING
```

`hashverify` exits non-zero if anything failed or is missing, so it drops into a script.

## Save-file conversion (PlayStation 1)

Convert a PS1 memory card between the container formats emulators and save tools use: the
raw 128 KB image (`.mcr` / `.bin` / `.mcd` / `.mem`), the DexDrive `.gme`, and the
Connectix VGS format. It is a container transform — the 128 KB of card data is preserved
byte-for-byte, and nothing inside the saves is decrypted.

```
dforge ps1card-convert <in> <out> [raw|gme|vgs]   # target defaults to the output extension
```

(Encrypted or signed single-save formats such as PSP `.vmp` are deliberately out of
scope — see the clean-room boundary in the README.)

## ROM header / interleave / byte-order fix-up

Older cartridge dumps often carry a copier header or a non-canonical byte order, which
is exactly what makes an otherwise-correct dump fail to match a No-Intro DAT (DiscForge
hashes the canonical headerless form for verification). `rom-convert` produces the
converted file so the dump matches:

```
dforge rom-convert <in> <out> <op>
  op: z64 | v64 | n64        # N64 byte order (big-endian / byte-swapped / little-endian)
      snes-strip | snes-add  # 512-byte SNES SMC/SWC copier header
      smd | unsmd            # Genesis SMD interleave  (smd = interleave, unsmd = flat .bin)
      nes-strip              # remove the 16-byte iNES header (+ trainer)
```

Each op is a lossless, reversible transform (`DiscForge.Core.Rom.RomConvert`).

## Save byte-order / size fix-up

Cartridge battery saves move badly between emulators for two reasons: N64 SRAM /
FlashRAM / EEPROM are stored in different word orders, and GB / SNES / Genesis SRAM
files differ only by trailing padding. `save-convert` applies the relevant reversible
transform — you pick which, since the right one depends on your emulators:

```
dforge save-convert <in> <out> <op> [--fill 00|FF]
  op: swap16 | swap32                 # word-swap byte order (self-inverse)
      pad <size|sram|flash|eeprom4k|eeprom16k|mempak>   # normalise to an exact size
      trim                            # strip trailing padding
```

## DAT diff

When a preservation set updates, compare the two DAT revisions to see exactly what
changed — added and removed games, and games whose catalogued dump was re-hashed:

```
dforge dat-diff <old.dat> <new.dat>
```

## Collection report

Turn a folder scan into a shareable, self-contained HTML dashboard — verified / misnamed
/ duplicate / unknown counts, the full file table, and what's missing from the set:

```
dforge library-report <dir> <out.html> [--dat <file>]
```

