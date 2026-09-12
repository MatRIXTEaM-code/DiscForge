# DiscForge — What's New (v1.90.2 → v1.95.0)

## Fixed a crash when launching an external dumping tool

Using the "Wii/GameCube discs…" button to launch RawDump2 was crashing the tool immediately, before its window even opened. The problem was that the tool was being started from the wrong folder, which broke its ability to find its own files. This is now fixed — external tools launch from their own folder correctly.

As a safety net, if a tool still fails to launch for any reason, DiscForge now forgets the broken path automatically, so you're prompted to locate the tool again next time instead of hitting the same failure over and over.

## Added support for more external ripping tools

The Read screen now has buttons to launch several well-known external disc tools directly from DiscForge, for cases where you'd rather use a trusted tool you already have, or where DiscForge's own reading can't get past a particular disc:

- **Wii/GameCube discs** — RawDump2
- **PS1 discs** — CloneCD
- **DVD discs** — Xreveal
- **Blu-ray discs** — CloneBD
- **IsoBuster** — general-purpose disc reading/extraction

Each remembers the location of the tool you point it at, so you only need to browse for it once. After running one of these tools, use "Import from external tool…" to bring the finished image into your library.

## Fixed burning CloneCD (.ccd) images

If you dumped a PS1 disc with CloneCD, DiscForge's Burn screen would refuse to burn the resulting `.ccd` image. Burn now recognizes `.ccd` files, automatically converts them into a standard image behind the scenes, and burns that — no manual conversion needed.

## Added external burning tools to the Burn screen

ImgBurn, Alcohol 120%, and DAEMON Tools were originally added to the Read screen, but since these are primarily burning and disc-mounting tools rather than ripping tools, they've been moved to where they actually belong: the Burn screen. If DiscForge's own burn engine can't handle a particular image, you can now launch one of these instead, right from Burn.

## Small naming fixes

Corrected "XReveal" to the tool's proper name, "Xreveal," everywhere it appears — the button, the file picker, and related text. The Wii/GameCube button now names RawDump2 specifically instead of just referring to it generically, matching how every other external-tool button is labeled.
