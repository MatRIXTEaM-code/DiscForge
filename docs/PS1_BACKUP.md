# Backing up and patching PlayStation 1 games

A PlayStation 1 disc is a CD-ROM, and DiscForge already has every piece needed
to back one up faithfully and to apply the patches the PS1 scene ships as PPF
files. This document ties those pieces into the one workflow: **read the disc to
an image, patch the image, verify, keep it.** Everything here concerns discs you
own; DiscForge detects protection and preserves it, and never circumvents it.

## What makes a PS1 disc different

Three things a backup has to respect:

1. **Mode 2 data, 2352-byte sectors.** PS1 data tracks are Mode 2 (Form 1 for
   game data, Form 2 for streamed XA audio). Faithfully imaging one means
   reading the full 2352-byte raw sector, not the cooked 2048-byte user area —
   otherwise the Form 2 XA sectors and the sub-header are lost. DiscForge's read
   planner forces raw 2352 whenever a disc is not plain data, and `--raw` forces
   it always (see docs/READING.md).

2. **Red Book audio tracks.** Many PS1 games are multi-track: a Mode 2 data
   track followed by CD-DA music. Those must be read as raw 2352 audio (they
   have no cooked form), and DiscForge does this automatically once it sees an
   audio track in the TOC.

3. **LibCrypt on some PAL titles.** LibCrypt hides a key in *deliberately
   corrupted subchannel Q* — a cluster of frames with bad Q-CRC at known
   sectors. The game checks the key; a copy that regenerates clean subchannel
   fails it. Backing such a title up correctly means **capturing the subchannel
   as a sidecar** so the corruption is preserved verbatim. DiscForge detects the
   LibCrypt fingerprint (`ProtectionScanner`, the Sub-channel view / `dforge
   subch`) and captures raw subchannel (`SubchannelCapture`, the `.sub`
   sidecar). It preserves the key; it does not defeat the check.

## Step 1 — read the disc to an image

In the GUI, use **Read Disc**. Pick the drive, read the TOC, and confirm the
per-track plan shows raw 2352 for the data track (and for any audio tracks). For
a possibly-protected PAL title, enable subchannel capture so a `.sub` sidecar is
written alongside the image — the Recovery and Sub-channel views cover the
retry-and-capture path.

The natural output for a multi-track PS1 game is **BIN/CUE** — one BIN holding
every track's raw sectors, a CUE describing the layout. That is the form the PS1
emulator and burning world expects, and the form a PPF patch is written
against. DiscForge reads to CDI natively and converts CDI ↔ BIN/CUE losslessly
(`dforge convert`), or the copy/read path can target BIN/CUE directly.

If you already have a `.bin` dump, skip to step 2.

## Step 2 — check what you have

```
dforge inspect game.cdi           # or: cue-check game.cue
dforge scan-protection game.bin   # LibCrypt / SafeDisc / SecuROM fingerprints
dforge subch game.sub             # if you captured subchannel: Q-CRC + LibCrypt
```

`scan-protection` tells you whether the title carries LibCrypt (and therefore
whether the `.sub` sidecar matters), and `subch` confirms the captured
subchannel actually holds the intentional corruption rather than read noise.

## Step 3 — patch it (PPF)

Translation patches, PAL→NTSC and NTSC→PAL region fixes, un-crippling patches
and fan hacks are almost all distributed as **PPF** files — the format
PPF-O-Matic and the PPF Patch Engine apply, and the form PAL region patchers
(PAL4U and its kin) ship their edits in. DiscForge applies all three PPF
revisions and can build PPF 3.0 itself.

GUI: open **PPF Patch**, drop the `.ppf` and the `.bin` on it, and click *Apply
patch*. Validation is on by default, so a patch built for a different dump
(wrong region, wrong rip) is refused before it touches your image, with a plain
explanation of the mismatch.

CLI:

```
dforge ppf-info  translation.ppf              # version, description, size, flags
dforge ppf-apply translation.ppf game.bin     # applies in place, validating first
dforge ppf-apply translation.ppf game.bin --dry-run   # check without writing
dforge ppf-apply translation.ppf game.bin --undo      # revert (PPF 3.0 w/ undo)
```

Notes:

- **Patch the BIN, not the CUE.** A PPF edits the raw image bytes; offsets in a
  PS1 patch are into the BIN. Leave the CUE alone.
- **Work on a copy.** `ppf-apply` edits in place. Keep the original dump; keep
  the `.ppf` too, because a PPF 3.0 patch with undo data can be cleanly
  reverted later (`--undo`), and the patch is what remembers how.
- **Validation is your friend.** If a patch refuses your image, it is almost
  always the wrong dump for that patch (different region or a bad rip), not a
  DiscForge problem — the validation block is a 1024-byte fingerprint of the
  exact image the patch author built against. `--force` overrides it, at your
  own risk.

## Step 4 — build your own patch (optional)

If you have an original image and a modified one of the same length, DiscForge
builds the PPF 3.0 that turns one into the other — the PPF-O-Matic "creator"
job:

```
dforge ppf-create original.bin modified.bin mypatch.ppf \
    --desc "Silent Hill PAL 60Hz fix" --fileid "by me, 2026"
```

By default the patch carries undo data (so it can be reverted) and a validation
block (so it refuses the wrong image). `--no-undo` / `--no-validation` produce a
smaller, plainer patch if you need one.

## Step 5 — verify and keep

```
dforge verify game.cdi --checksums      # structural + per-track CRC-32
dforge checksum game.bin                # MD5 / SHA per image and track
```

Record the checksums with the backup. If you split it for a FAT32 stick
(`dforge split`), the manifest carries a SHA-256 so a bit-rotted part is caught
by name rather than discovered as a bad burn.

## Where DiscForge stops

DiscForge images the disc, preserves LibCrypt's subchannel key verbatim, and
applies the patch files you already hold. It does **not** strip protection,
generate key-defeating patches, or bypass any check — a LibCrypt title is backed
up *with* its key intact, exactly as pressed, so the copy behaves like the
original. That boundary is deliberate and permanent (see docs/COMPARISON.md,
section 13).
