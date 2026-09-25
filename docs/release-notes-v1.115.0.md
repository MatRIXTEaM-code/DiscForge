# DiscForge 1.115.0

Released 25 September 2026

## DiscForge is now free

DiscForge is now free to use on as many PCs as you like, at home or at work. There is no trial,
no licence key and no reminder screen any more. If you'd like to support the project, there is a
Donate button in the About box. Donations are optional and don't unlock anything.

## New

**Apply xdelta patches.** The Patch screen can now apply xdelta patches, the format most PS1,
PS2 and GameCube translation and fan patches come in. Large DVD images are fine. If you pick the
wrong original image, DiscForge stops and tells you instead of making a broken file. A few
patches use a compression type DiscForge can't read yet. For those, the new "xdelta tool" button
opens Delta Patcher, xdelta UI or xdelta3 instead.

**Create xdelta patches.** "Create patch" can now also make xdelta patches, which anyone can
apply with xdelta3, Delta Patcher, xdelta UI or DiscForge.

**More GameCube RVZ and WIA files open.** Files compressed with LZMA or LZMA2 now open, as well
as the usual zstd and uncompressed ones.

**Import a whole dump.** "Import from external tool" on the Read Disc screen now accepts a .cue
or .gdi file. It copies the sheet, every track file and the dump's log together, and tells you if
a track file is missing.

**Dump logs are checked on import.** If the dump came with a redumper or DiscImageCreator log,
DiscForge shows what the log recorded (tool version, drive and read errors). For redumper dumps it
also checks each imported file against the checksum in the log.

**Buttons for the tools you already use.** DiscForge can now open these from the matching screen:

- Read Disc: redumper, MPF, and an "Other tool" button for anything else
- Rip Audio: Exact Audio Copy and CUERipper
- Disc quality scan: QPxTool and Opti Drive Control
- AccurateRip: CUETools
- Mount: WinCDEmu, for BIN/CUE and audio discs Windows can't mount
- VOB Demux: MKVToolNix
- Sector view: your hex editor
- Dreamcast: Universal Dreamcast Patcher
- Sets: your ROM manager (RomVault, clrmamepro or igir)
- ScummVM: "Play in ScummVM" opens the game straight away

DiscForge doesn't include any of these tools. You point it at your own copy once and it remembers.

## Fixed

- **Opening RVZ and WIA files made by other tools:** fixed a bug that put the data in the wrong
  place.
- **Labels running into boxes:** fixed on the Dump Certificate, Dump Certificate Ledger and
  Recovery screens.
- **The "Open" button on the PS1 asset screen:** its text is no longer cut off.
