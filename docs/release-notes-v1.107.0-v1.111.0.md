# DiscForge — What's New (v1.107.0 → v1.111.0)

## Added a "Format Media" tile

If you use a flashcart-based dumper (GBxCart RW, Cart Reader, or a floppy-imaging rig), you now
have a dedicated tile for prepping the SD/SDHC/SDXC card it reads from — it launches a card
formatter (like the SD Association's official SD Card Formatter) right from DiscForge. DiscForge
doesn't have its own card-formatting code and isn't trying to grow any — this just gives you a
one-click way to get to the tool that already does that job properly.

## Added a "Raw Copy" tile

A second new tile for a different job: cloning a whole physical drive or disk image byte-for-byte
(drive-to-image, image-to-drive, or drive-to-drive). This is a different kind of work from
anything else in DiscForge — everything else is built around optical-disc/cartridge/floppy
structure — so it launches a dedicated tool (like HDD Raw Copy Tool) rather than trying to
reinvent generic disk cloning.

## Fixed: external-tool buttons could get permanently stuck

If you pointed one of DiscForge's "launch an external tool" buttons at the wrong file — say, a
Windows shortcut-icon leftover instead of the real program — it would happily "launch" that file
forever afterward with no way to fix it, since the button only forgets a bad path when the launch
actually fails with an error. Now you can **hold Shift while clicking** any external-tool button to
make it ask you to pick the file again, even if it already has one remembered. This applies
everywhere in the app: Format Media, Raw Copy, and every rip/burn/Xbox/memory-card/cartridge/floppy
tool button.

## Added GUI screens for three CLI-only features

Three features that previously only worked from the command line now have full screens in the app:

- **Dump Certificate Ledger** — a public, tamper-evident log where independent dumps of the same
  disc get cross-checked against each other, so you can see whether strangers' rips of a title
  agree without having to trust any single person's word for it.
- **Media Mortality** — a shared, privacy-preserving model of how fast a type of disc actually
  decays over time, built from many people's observations pooled together with no raw data shared.
- **Disc MRI's re-read planning** — you can now generate and save a targeted re-read plan for a
  damaged disc's worst sectors directly from the Disc MRI screen, instead of only through the CLI.

## Made dump completeness checking actually check every sector

DiscForge has long had a "completeness check" that tells you whether a bin/cue dump is whole. It
used to only add up totals — does the file size match the expected number of sectors, does the
subchannel sidecar cover the same number of sectors. That misses a real, if rare, problem: a
corrupted or badly hand-edited cue sheet where two tracks' start points overlap or skip a range,
while the totals still happen to add up correctly. The completeness check now walks every track's
own declared position and confirms nothing is silently missing or double-counted — a stronger
guarantee than "the numbers add up."

## Confirmed on real hardware: interrupted rips resume correctly

DiscForge can resume a rip that gets interrupted partway through (power blip, accidental Ctrl+C,
whatever) instead of starting over from scratch. This has been tested against a real drive now,
not just in a lab-style test: a rip was deliberately killed partway into a long track, and
resuming it correctly picked up where it left off — reusing the track that had already finished
instead of re-reading it — and finished with a clean, verified result.

## Everything else

No regressions found in the existing test suite (2,732 tests green). `DiscForge.App` still
requires a real Windows build environment to compile and run — confirmed working via a live
click-through and hardware test this round.
