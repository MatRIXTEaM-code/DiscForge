# Alcohol 120% MDS/MDF support

An Alcohol image is a pair:

- **`.mds`** — Media Descriptor: header, session blocks, track blocks, extra
  blocks, footers, filenames. Small.
- **`.mdf`** — Media Data File: the raw track data, addressed by byte offsets
  recorded in the descriptor.

`dforge convert disc.mds disc.cdi` converts one to CDI. The `.mdf` is found beside
the `.mds` by convention; `--mdf <path>` overrides that.

## Clean-room

The layout comes from **public format documentation** (libmirage's MDS parser and
the long-circulated format notes). No Alcohol binary was disassembled and no
licensed build was inspected — the same standard applied to CDI throughout.

**Explicitly not implemented:** Alcohol's copy-protection layer (SafeDisc /
SecuROM / StarForce profiles, RMPS emulation, DPM). Those are circumvention
tools; making one is an offence under CDPA s296ZB. They're also irrelevant to
imaging your own unencrypted media.

## Structures

Sizes: header 0x58, session block 0x18, track block 0x50, extra block 8,
footer 16 — all little-endian.

Track modes: `0xA9` audio, `0xAA` Mode 1, `0xAB` Mode 2, `0xAC` Mode 2 Form 1,
`0xAD` Mode 2 Form 2, `0xEC` Mode 2 mixed.

**The awkward part:** a session's track array holds *lead-in descriptors*
(points `0xA0` first track, `0xA1` last track, `0xA2` lead-out) alongside real
tracks (points 1..99). Their payload sits in `pmin/psec/pframe` as **MSF, not
LBA** — so the lead-out position must be decoded through an MSF conversion where
LBA 0 == 00:02:00. Getting that wrong shifts the lead-out by up to 74 sectors,
silently. (The reference implementation had exactly that bug — dropped frames —
and the round-trip test caught it.)

Pregap sectors are **stored** in the MDF, so a track occupies
`(pregap + length) * sectorSize` bytes and the next track's offset accounts for it.

## Conversion limits (refused, not silently mangled)

- **2448-byte sectors** (2352 + 96 bytes of interleaved P-W sub-channel): CDI has
  no sub-channel form, so this is refused rather than dropping data.
- Sector sizes other than 2048 / 2336 / 2352 are refused.
- If the `.mdf` is too short for what the descriptor claims, conversion stops —
  a mismatched pair must not yield a silently truncated image.

## Validated

- `docs/reference/mds_format.py` — builds and parses MDS structures: single data
  track, audio with accumulating MDF offsets, mixed mode, MSF round-trips across
  the CD range, and rejection of rubbish input.
- C# constants, block sizes, track-mode values and the MSF offset are
  cross-checked against that reference.
- Conversion is proven end to end: MDS/MDF -> CDI -> parsed back by our own
  parser, payload byte-identical.

## Not done yet

- **Never tested against a file produced by Alcohol itself.** The layout is from
  documentation; a genuine `.mds` is the outstanding test — exactly the position
  the CDI parser was in until a real DiscJuggler image proved it.
- Writing MDS/MDF (export) is not implemented; only reading.
- DPM blocks, BCA and disc structures are parsed past, not interpreted.
- Multi-session MDS files are parsed but untested against real examples.
