# Front-end & emulator library export

DiscForge already identifies every image in a folder and knows its CRC-32; this
feature writes those facts in the dialect each popular front-end reads, so a
verified collection drops straight into a library. It is pure cataloguing — the
output is playlist/metadata text, never modified game data, and nothing here touches
protection.

Three targets are covered, chosen because between them they serve the whole common
stack (RetroArch, LaunchBox-style scanners, EmulationStation, RetroBat, and the
stand-alone emulators DuckStation / PCSX2 / Dolphin that honour M3U):

**RetroArch playlist (`.lpl`).** The modern JSON playlist. Each entry carries the
image path, a display label (the DAT game title when a DAT is supplied, otherwise the
file stem), the CRC-32 in RetroArch's `XXXXXXXX|crc` form so its scanner can match a
core, and `DETECT` cores so RetroArch chooses one. Build it with:

```
dforge frontend-export retroarch <folder> <out.lpl> [--name <playlist name>] [--dat <file>]
```

**EmulationStation / RetroBat `gamelist.xml`.** A `<game>` per image, with a relative
`./name` path (so the list is portable inside its ROM folder), the name, and the
detected system as `<desc>`. Build it with:

```
dforge frontend-export gamelist <folder> <out.xml> [--dat <file>]
```

**Multi-disc `.m3u`.** RetroArch, DuckStation and PCSX2 load an M3U instead of a
single disc so disc-swapping and a shared memory card work. Give the discs in order:

```
dforge m3u <out.m3u> "<Game (Disc 1).chd>" "<Game (Disc 2).chd>" ...
```

Discs that sit beside the `.m3u` are written as bare filenames (portable); others keep
the path given. Both the folder export and the M3U builder are also on the **Playlists**
GUI tile — scan a folder and export, or assemble a multi-disc M3U with up/down ordering.

Sidecar files in a scanned folder (`.m3u`, `.lpl`, `.xml`, `.txt`, `.dat`, `.nfo`,
`.sbi`, `.sub`, image thumbnails) are skipped so they never appear as fake games.

The generators live in `DiscForge.Core.Frontend.FrontendExport` (`BuildRetroArchLpl`,
`BuildEmulationStationGamelist`, `BuildM3u`) and are covered by the offline test suite.
