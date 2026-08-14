# Backing up and patching PlayStation 2 games

A PlayStation 2 disc is either a CD (early titles) or a DVD (most of the
catalogue), and backing one up is the same shape as any other disc: image it,
patch it, verify it. DiscForge already has every piece — the read path handles CD
and DVD, PPF patching is done, and a PS2 disc identifier reads the game's serial
and region from its own SYSTEM.CNF. This concerns discs you own; DiscForge images
faithfully and applies patch files you hold, and it decrypts nothing — a PS2
disc's data is not encrypted.

## Two kinds of PS2 disc

1. **DVD games** — the majority. A standard DVD: ISO 9660 + UDF filesystem,
   cooked 2048-byte sectors. DiscForge's read planner reads DVD media cooked at
   2048 (raw 2352 is a CD-only concept and is refused for DVD/BD up front). The
   natural image is a plain `.iso`.

2. **CD games** — early or budget titles, on a blue-bottomed CD. Mode 2 like a
   PS1 disc, read raw 2352. BIN/CUE (or CDI) is the natural image.

DiscForge reads both; the read plan shows which it chose per the media the drive
reports.

## Step 1 — identify the disc

```
dforge ps2-info game.iso        # or game.cdi / game.bin
```

This reads **SYSTEM.CNF** from the disc's filesystem and reports the console
(PS1 or PS2), the game serial (e.g. `SLUS-20002`), the region (from the serial's
third letter — U = USA, E = Europe, P = Japan, K = Korea), the video mode
(NTSC/PAL) and the boot executable. It confirms you imaged the disc you meant to,
and gives you the serial to match a patch or a redump entry against.

## Step 2 — image the disc

Use **Read Disc**. For a DVD title the plan will read cooked 2048; for a CD title
it reads raw 2352. Read it to CDI (convertible to ISO or BIN/CUE with
`dforge convert`), or read/convert to the form your patch expects. A DVD image is
usually kept as `.iso`; a CD image as BIN/CUE.

## Step 3 — patch it (PPF)

Translations, undubs, region and cheat patches for PS2 games are distributed as
**PPF**, the same format as PS1 (see docs/PS1_BACKUP.md). Apply the patch to the
disc image:

```
dforge ppf-info  patch.ppf
dforge ppf-apply patch.ppf game.iso        # applies in place, validating first
dforge ppf-apply patch.ppf game.iso --dry-run
dforge ppf-apply patch.ppf game.iso --undo # revert (PPF 3.0 with undo)
```

Work on a copy, keep the original image and the patch, and let validation guard
you — a patch built for a different release (different serial or revision) is
refused before it touches your image.

## Step 4 — verify and keep

```
dforge verify game.cdi --checksums       # if kept as CDI
dforge checksum game.iso                  # MD5 / SHA of the image
```

Record the checksums with the backup, and match the serial from `ps2-info`
against a redump entry if you want to confirm a clean dump.

## Where DiscForge stops — and what stays out

DiscForge images the disc, identifies it, and applies the patch files you hold. It
does **not** do the things that make an unmodified console *boot* a backup — the
disc-swap / ESR / modchip methods that get around PS2's disc-recognition check.
That check is a positional/structural signature the console verifies, not an
encryption of the data; a faithful image preserves the data, and defeating the
boot check is circumvention, which DiscForge does not do (docs/COMPARISON.md §13).
As on every other console: image faithfully, patch with what you hold, don't
bypass protection.
