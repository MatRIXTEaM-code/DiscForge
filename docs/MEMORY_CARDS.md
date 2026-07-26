# Console memory-card support

DiscForge reads the save cards of all three consoles it covers on the disc side —
the same "list and extract a filesystem" work it does for discs, applied to saves.
Every card format here is a plain, unencrypted filesystem; nothing is decrypted or
circumvented, and where a per-file copy-protect flag exists it is *reported*, not
defeated (extraction honours it unless explicitly forced).

## PlayStation 1 — `.mcr` / `.mcd` (`PsxMemoryCard`)

The format pSX and other emulators use. 128 KB = 16 blocks of 8 KB; block 0 is a
directory (an "MC" header plus 15 entries), and a save is a chain of linked 8 KB
blocks whose first block opens with an "SC" title header.

```
dforge psxmc-info    card.mcr          # list saves (product code, blocks, title)
dforge psxmc-extract card.mcr out/     # extract each save as a .mcs block image
```

## PlayStation 2 — `.ps2` (`Ps2MemoryCard`)

The "MyMC" job. An 8 MB card of 1 KB clusters (raw, or 528-byte pages with ECC),
a double-indirect FAT, and a directory tree in which each save is a folder of
files (icon.sys, the save data, …).

```
dforge ps2mc-info    card.ps2          # list saves and their files
dforge ps2mc-extract card.ps2 out/     # extract every save folder
```

## Dreamcast — VMU / VMS (`VmuImage`, `VmuBuilder`, `Vmi`)

The "VMU Tool" job — and DiscForge can *write* this one too. A 128 KB card of 512-
byte blocks with a FAT and a 32-byte-entry directory; saves are VMS files.

```
dforge vmu-info    card.bin            # list saves
dforge vmu-extract card.bin out/       # extract each save as a .VMS
dforge vmu-create  card.bin            # a blank formatted card
dforge vmu-add     card.bin save.vms   # add a save
dforge vms2vmi     save.vms out.vmi    # write the VMI download descriptor
```

## Validation & scope

Each reader is validated by building a structurally complete card by hand — header,
FAT/allocation, directory, and save data — and reading it back, so the directory
decode, the block/cluster-link chains and extraction are all pinned by tests
(the VMU also round-trips through its writer). Honest limits: PS1 save titles are
read as ASCII (full-width/kanji glyphs are not transcoded); PS2 and PS1 *writing*
(injecting saves back into a card) is a natural follow-up — only the Dreamcast VMU
writer exists so far. Nothing here decrypts or defeats protection, consistent with
the clean-room rule (docs/COMPARISON.md §13).
