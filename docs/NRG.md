# Nero NRG support

NRG is Nero Burning ROM's disc image format. DiscForge reads and writes NRG v2
(the "NER5" format) and converts it to and from CDI, so a Nero image can be
inspected, converted, patched (via BIN/CUE or CDI) and rebuilt within the same
toolchain.

```
dforge convert image.cdi image.nrg      # CDI -> Nero NRG v2
dforge convert image.nrg image.cdi      # Nero NRG -> CDI (--version v2|v3|v35)
```

## The container (clean-room, from the public format description)

NRG puts the raw track data at the front of the file and a chunk-based table of
contents at the back, reached through a footer at the very end:

```
Footer (v2, last 12 bytes of the file):
  "NER5" + 64-bit big-endian offset to the first chunk
(v1 uses "NERO" + a 32-bit offset.)

Chunks (from that offset, until "END!"):
  4-byte ASCII tag + 32-bit big-endian length + payload

  CUEX  the cue table — 8-byte entries: control, track, index, 0, then a
        32-bit big-endian LBA. Carries each track's start LBA.
  DAOX  Disc-At-Once info — the authoritative track table: a header
        (redundant size, UPC, first/last track), then one 42-byte entry per
        track holding sector size, mode code, and the index0 / index1 / end
        byte offsets into the file's data region.
  END!  marks the end of the chunk list.
```

The reader combines the two: DAOX gives each track's sector size, mode and data
offsets (length = end − index1, in sectors), and CUEX gives the start LBA.

## Conversion

`NrgConverter` moves tracks between NRG and CDI without altering a sector: mode,
sector size and absolute start LBA carry across. NRG has no session concept, so
a multi-session CDI flattens to one track list in NRG — but the absolute LBAs are
preserved, so nothing about the layout is lost, and CDI → NRG → CDI reproduces
every track's mode, size, LBA and data. (A full GDI → CDI → NRG → CDI → GDI chain
preserves track data byte-for-byte.)

## Validated — and the honest limit

Like the CDI reader, which still awaits a real DiscJuggler descriptor to validate
its richest variants, the NRG support here is **validated by round trip**:
DiscForge's writer and reader agree on the container, and CDI ↔ NRG conversion
preserves tracks and data. It has **not yet been validated against images
produced by Nero itself** — that needs a sample `.nrg`, and is the next step. The
structure follows the public NER5 format description; if a real Nero image reveals
a field this reads differently, that is a bug to fix against the sample, not a
design change.

Scope: **NRG v1 (NERO) and v2 (NER5)** read and write. v1 uses the "NERO"
footer, 32-bit offsets, and CUES (MSF addresses) / DAOI (30-byte) chunks; v2 uses
"NER5", 64-bit offsets, and CUEX / DAOX. Both are round-trip validated (writer ↔
reader); real-Nero validation remains pending a sample. Sub-channel (2448-byte)
and CD-TEXT chunks are not carried.
