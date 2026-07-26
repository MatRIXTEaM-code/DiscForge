# Backing up and patching Dreamcast games

A Sega Dreamcast game is a GD-ROM — a CD-derived disc with a twist that shapes
everything about backing one up. This document covers what DiscForge does today
(read and validate GD-ROM images, patch them with PPF), and is honest about the
one step it cannot do from a PC and the two features still to come. As always,
this is faithful imaging and patch application for discs you own; a GD-ROM
carries no encryption, so nothing here decrypts anything — there is nothing to
decrypt.

## What makes a GD-ROM different

A GD-ROM has two areas on the one disc:

1. **The low-density area** — an ordinary CD region any drive can read. It holds
   the "this disc is for the Dreamcast" warning track and usually a short audio
   track. Tracks 1 and 2 live here, at low LBAs.

2. **The high-density area** — a second, denser recording starting at **LBA
   45000** that holds the actual game (~1 GB). This is the Dreamcast's trick:
   only the console's own GD drive can read this area. A standard PC optical
   drive physically cannot.

That second fact is the important one. **DiscForge cannot dump the high-density
area from a physical GD-ROM**, because no PC drive can read it — that has always
needed the console itself (a Dreamcast with a serial or broadband adapter, or a
specific drive trick). DiscForge works with the resulting **images**, which is
how Dreamcast backups are stored and shared.

## Image formats: GDI and CDI

Dreamcast images come in two shapes, and DiscForge handles both:

- **CDI** (DiscJuggler) — the format DiscForge was built around. `dforge inspect`,
  `verify`, `extract`, `convert` and the whole CDI toolchain apply directly.
- **GDI** — a plain text index (`disc.gdi`) that lists the tracks, paired with
  one binary file per track. `dforge gdi-info` reads and validates it.

A `.gdi` index is one line of track count, then one line per track:

```
3
1 0     4 2352 track01.bin 0     ← low-density data (warning track)
2 600   0 2352 track02.raw 0     ← low-density audio
3 45000 4 2352 track03.bin 0     ← high-density DATA: the game
```

The fields are: track number, start LBA, type (4 = data, 0 = audio), bytes per
sector, the track's file, and a byte offset into that file.

## Step 1 — check the image

```
dforge gdi-info disc.gdi
```

This parses the index, validates it against the track files beside it (present,
whole number of sectors, LBAs ascending), and identifies the **high-density data
track** — the one at LBA ≥ 45000 that holds the bootable game filesystem. That is
the track a patch targets. For a CDI image, use `dforge inspect disc.cdi` and
`dforge verify` as with any CDI.

## Step 2 — patch it (PPF)

Region-free patches, VGA-output enablers and fan translations for Dreamcast games
are shipped as **PPF**, the same format as PS1 patches (see docs/PS1_BACKUP.md).
They are applied to the **high-density data track** — the game track from
`gdi-info` (usually `track03.bin`), or the data track inside a CDI.

```
dforge ppf-info  patch.ppf                # what the patch is
dforge ppf-apply patch.ppf track03.bin    # applies in place, validating first
dforge ppf-apply patch.ppf track03.bin --dry-run
dforge ppf-apply patch.ppf track03.bin --undo   # revert (PPF 3.0 with undo)
```

Notes:

- **Patch the game track, not the index.** The `.gdi` is a table of contents;
  leave it alone. The PPF edits the bytes of the high-density data track.
- **Work on a copy**, and keep the original track file. A PPF 3.0 patch with undo
  data can be reverted later, and the patch is what remembers how.
- **Validation guards you.** If a patch refuses your track, it is almost always
  built for a different dump (different region or revision) — the validation
  block is a fingerprint of the exact image the author patched. `--force`
  overrides it, at your own risk.

## Browse the game filesystem — `dforge gdi-browse`

The high-density data track carries an ISO 9660 filesystem, addressed from the
track's start LBA (45000) rather than from zero. DiscForge reads it directly:

```
dforge gdi-browse disc.gdi                 # list the game's files
dforge gdi-browse disc.gdi --extract out/  # write them out
```

It cooks the game track's user data on the fly (handling raw 2352 Mode 1 or
cooked 2048 sectors) and reads the ISO with the base-LBA reader, so the files
list and extract exactly as on any other disc. Also exposed as a standalone
"ISO LBA Fix": `dforge iso-rebase in.iso out.iso 45000` shifts an ordinary ISO's
addresses to the GD-ROM base, the operation older tools called an LBA fix.

## GDI ↔ CDI conversion — `dforge convert`

Both Dreamcast image formats hold the same track data, so DiscForge re-containers
between them without touching a sector:

```
dforge convert game.gdi game.cdi          # GDI -> CDI (--version v2|v3|v35)
dforge convert game.cdi game.gdi          # CDI -> GDI (index + one file per track)
```

The GD-ROM's two-area layout is preserved: the low-density tracks (LBA < 45000)
and the high-density game (LBA ≥ 45000) become two CDI sessions with the large
LBA gap carried as metadata, so a GDI → CDI → GDI round trip reproduces the index
and every track file byte-for-byte. Data tracks come back as `.bin`, audio as
`.raw`. (CDI → GDI note: a CDI track's separate pregap, if any, isn't expressed by
.gdi and is folded into the track from its start — the same limitation BIN/CUE has;
its data is preserved.)

Dreamcast support is now end-to-end: read/validate (`gdi-info`), browse the game
filesystem (`gdi-browse`), patch with PPF, and convert between GDI and CDI.

And one thing that is deliberately **out of scope, permanently**:

- **Self-booting burnable copies** (the MIL-CD boot exploit). Making a copy the
  console boots from unsigned media is a boot-signature bypass — circumvention,
  not preservation — and DiscForge does not do it, the same clean-room line it
  holds everywhere else (see docs/COMPARISON.md, section 13). DiscForge images,
  validates and patches; it does not defeat boot security.

## Where DiscForge stops

It reads and validates GD-ROM images (GDI and CDI), and applies the PPF patch
files you already hold to the game track. It does not read the high-density area
from a physical disc (no PC drive can), and it does not produce self-booting
media. Everything it does is on the faithful-imaging, patch-application side of
the line.
