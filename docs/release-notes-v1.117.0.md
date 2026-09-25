# DiscForge 1.117.0

Released 25 September 2026

## New

**More old archive types.** Besides ACE, DiscForge now opens the other archive formats that DOS,
Amiga and early Windows software came in:

- **LHA and LZH** files, from LHarc, LHA, Amiga LhA and the Japanese LHA tools, plus LArc (.lzs).
- **ARJ** files, including split sets (.arj, .a01, .a02 …) and password-protected ones.
- **ZOO** files.

Self-extracting .exe versions of these open too, without running them. As with ACE, DiscForge only
reads these formats, checks every file before saving it, and never writes outside the folder you
choose.

**Check a whole folder.** The new "Check a folder…" button on the Extract screen looks through a
folder (and the folders inside it) for old archives, tests each one and tells you which are fine,
damaged, missing a part or need a password. You can save the results as a spreadsheet file, or
extract all the good ones in one go, each into its own folder.

**Archives on disc images.** Drop an ISO, BIN/CUE or CDI on the Extract screen and DiscForge lists any
old archives stored on the disc. You can extract them straight from the image without copying them
out first. Useful for old magazine cover discs and shareware CDs.

**Right-click in Explorer.** The installer can add "Extract with DiscForge" and "Open in DiscForge" to
the right-click menu for .ace, .lzh, .lha, .arj and .zoo files. It can also make DiscForge the
program that opens them when double-clicked. That's off by default, so WinRAR or 7-Zip stay in charge
if you use them.

**New commands:** `dforge unpack`, `unpack-list`, `unpack-test`, `unpack-sweep` (check a folder) and
`unpack-image` (archives on a disc image).

## Good to know

A few very rare LHA variants (-lh2-, -lh3- and PMarc) aren't supported yet. DiscForge says so rather
than producing a broken file.

## Thanks

LHA support draws on Lhasa by Simon Howard (ISC licence), and ACE support on acefile by Daniel
Roethlisberger (BSD licence).
