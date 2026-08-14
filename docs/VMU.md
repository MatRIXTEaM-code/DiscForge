# Dreamcast VMU (memory card) support

The "VMU Tool" / "VMU Dream Explorer" job: read a Sega Dreamcast VMU (Visual
Memory Unit) flash dump, list its saves, and extract each as a `.VMS` file. This
complements DiscForge's Dreamcast GD-ROM (GDI) support with the console's *save*
side. It is plain filesystem work on a person's own memory-card dump — no
protection is defeated; the per-file copy-protect flag is *reported*, and an
extract honours it unless explicitly forced.

```
dforge vmu-info    card.bin              # list the saves on a 128 KB VMU dump
dforge vmu-extract card.bin out/         # extract every save as a .VMS
dforge vmu-extract card.bin out/ --force # include copy-protected saves (your own)
```

## The filesystem (clean-room, from public documentation)

A VMU is 256 blocks of 512 bytes (128 KB). The layout is fixed:

```
block 255       root  — validation (0x55…), plus the FAT/directory locations & sizes
block 254       FAT   — 256 little-endian 16-bit entries
blocks 253-241  directory — 32-byte entries, descending
blocks 0-199    user file data
```

The FAT entry values are `0xFFFA` (last block of a file), `0xFFFC` (free), or the
next block in the chain. A directory entry gives the file type (`0x33` data save,
`0xCC` mini-game), the copy-protect flag, the first block, a 12-character name, the
size in blocks, and where the VMS header sits within the file. A file's bytes are
the FAT chain walked from its first block; DiscForge reads the VMS header's 16- and
32-character descriptions for the listing.

## Validated

`VmuImage` is validated by building a formatted card by hand — root, FAT, a
two-block save with a VMS header — and reading it back: the directory decode, the
FAT-chain extraction, the descriptions, the free-block count, and the copy-protect
refusal are each pinned by tests.

## Scope — honest

- **Reading and extraction** of saves from a raw 128 KB VMU dump: done.
- **VMS ↔ VMI** conversion (the download-wrapper metadata) and **writing** saves
  back into a card image are natural follow-ups, not yet built.
- The copy-protect flag is honoured, never circumvented — consistent with
  DiscForge's clean-room rule (docs/COMPARISON.md §13).
