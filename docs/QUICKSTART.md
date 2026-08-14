# DiscForge Quickstart

DiscForge is a large toolkit, but most people arrive wanting to do one of about ten things. This page maps each of those goals to the one or two commands that do it. Everything here is the command-line tool, `dforge`; run any command with no arguments to see its full options, and run `dforge` alone to list every command.

## The ten things people usually want

**1. Find out what a file is.** `dforge identify <file>` names it by its signature — any disc image, filesystem, console format, memory card, ROM, archive, or media type DiscForge knows (well over a hundred).

**2. Look inside a disc image.** `dforge browse <image>` (or `dforge ls <image>`) lists the files; add `--extract <dir>` to pull them out. Works across ISO 9660, Joliet, UDF, and — on a Mac+PC hybrid — the HFS side too.

**3. Check that a dump is good.** `dforge dump-audit <cue|image>` gives one plain verdict — GOOD / SUSPECT / BAD — fusing structure, holes, EDC/ECC, end-of-disc and pregap checks (add `--dat <file>` to also match a Redump/No-Intro entry). For an audio CD, `dforge accuraterip <image.cue>` computes the AccurateRip checksums that prove your rip matches everyone else's.

**4. Verify a CHD hasn't rotted.** `dforge chd-verify <image.chd>` decompresses every hunk, checks each map CRC, and confirms the whole image matches its stored SHA-1 — the same proof chdman performs, without extracting.

**5. Check a disc's filesystems are structurally sound.** `dforge iso-lint`, `dforge udf-lint`, `dforge fat-lint`, and `dforge hfs-lint` each validate one filesystem against its spec; `dforge fs-verify <image>` cross-checks that a bridge/hybrid disc's ISO and UDF views describe the same files.

**6. Build a data-disc image (optionally bootable).** `dforge iso-create <folder> <out.iso>` builds a standard ISO 9660 (Joliet on by default; `--rock-ridge` for POSIX names; `--boot <loader>` for an El Torito bootable disc). For UDF or a UDF/ISO bridge, use `dforge create-udf` or `dforge create-udf-bridge`; for an Xbox disc, `dforge create-xiso`; for a Dreamcast/CD image, `dforge create`.

**7. Convert between image formats.** `dforge convert <in> <out>` moves between `.cue`/`.bin`, `.iso`, `.chd`, `.cdi`, `.nrg`, and more. To *prove* a conversion lost nothing, `dforge verify-convert <a> <b>` decodes both to raw sectors and compares byte-for-byte.

**8. Compare two discs.** `dforge disc-diff <a> <b>` reports what changed at the file level — added, removed, changed, or moved — between two pressings, a patched vs original disc, or two revisions.

**9. Catalog a whole collection.** `dforge library scan <dir> --dat <file> --html report.html` identifies, hashes and verifies every file in a tree and writes a friendly dashboard. `dforge catalog-export <dir> --json catalog.json --csv catalog.csv` writes a portable index (identity, hashes, verification status) to keep beside a NAS or cloud backup so anything can find and re-verify a disc without re-reading it.

**10. Burn an image to a disc.** `dforge burn <image.iso>` writes a data ISO to a blank disc. On Windows give the target drive letter (`dforge burn game.iso E --verify`); on macOS it writes to the inserted disc via `hdiutil`; on Linux it uses `growisofs`/`wodim`. `dforge drives` lists your recorders.

## Platform support — what runs where

The **command-line tool (`dforge`) runs everywhere** — Windows, macOS, and Linux — and burning now works on all three (Windows via IMAPI2, macOS via `hdiutil`, Linux via `growisofs`/`wodim`). Note that most modern machines no longer ship an optical drive, so burning generally needs an external USB writer.

The **graphical app is Windows only.** Mac and Linux users work through the CLI, which covers the full toolkit.

## Finding your way around

- `dforge` with no arguments lists every command, grouped by area.
- `dforge <command>` with no arguments shows that command's usage and options.
- `dforge search <file> --ascii TEXT` (or `--hex`) searches inside a file.
- Most read-only commands accept `--json` for scripting.

Once a goal here points you at a command, its own `--help`-style usage line covers the rest. The full reference for every command is in [COMMANDS.md](COMMANDS.md).
