# CHD (Compressed Hunks of Data) — inspection

CHD is the compressed disc-image format the emulation ecosystem uses — MAME,
RetroArch, and ROM-manager libraries such as RomM store CD/DVD/GD-ROM images as
`.chd`. DiscForge reads a CHD's structure so it can identify one and report the
disc it holds.

```
dforge chd-info game.chd     # version, codecs, size, and the CD track layout
```

## What it reads (clean-room, from the public CHD v5 description)

The 124-byte big-endian header — magic `MComprHD`, version, the four compression
codecs (`cdzl` zlib, `cdlz` LZMA, `cdfl` FLAC, or `none`), the logical
(uncompressed) size, the hunk and unit sizes — and the metadata linked list, from
which the CD track descriptors (`CHT2`/`CHTR`, ASCII `TRACK/TYPE/SUBTYPE/FRAMES/
PREGAP/POSTGAP`) are parsed into a track list.

Validated by building a CHD v5 header plus a CD-track metadata entry by hand and
reading it back — the big-endian header decode, the FourCC codec names, and the
track-descriptor parse are each pinned by tests.

## Honest scope — inspection, not yet extraction

This identifies and describes a CHD; it does **not** yet decompress the hunk data.
Full extraction is a substantial follow-up with two hard layers: the v5 hunk map's
custom huffman-delta encoding, and the CD codecs themselves — `cdzl` (zlib, doable
with the built-in inflate), `cdlz` (LZMA, needs an LZMA decoder), and `cdfl` (FLAC,
needs a FLAC decoder). Because a partial decoder that only handled uncompressed or
zlib hunks would fail on most real CHDs (which use `cdlz`/`cdfl`), extraction is
deferred until it can be done properly and validated against real CHD samples,
rather than shipped half-working. For now, DiscForge tells you what a CHD contains;
converting one to BIN/CUE is the planned next step. Only CHD v5 (the current
format) is read; older CHDs should be re-created with a current `chdman`. Nothing
here is protection-related.
