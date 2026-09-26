# DiscForge 1.118.0

Released 26 September 2026

## New

**Rescue Disc.** A new screen for copying scratched or failing discs (data CDs, DVDs and Blu-rays).
It works the way the well-known GNU ddrescue tool does:

1. It copies all the readable parts of the disc first, in big fast reads. When it hits a damaged
   area it jumps past it instead of grinding away at it.
2. Then it goes back to the damaged areas and works inwards from their edges to rescue as much as
   possible.
3. Then it tries what's left one sector at a time.
4. Finally it retries the bad sectors as many times as you ask.

A bar shows the whole disc as it goes: green for rescued, grey for not tried yet, amber for areas
still being worked on, and red for bad sectors.

**Stop and carry on later.** Progress is saved in a small map file next to the image. You can stop at
any time and press Start again later, and it carries on where it left off.

**Try another drive.** Some drives read damaged discs better than others. Put the disc in a different
drive, pick the same image, and press Start. DiscForge only reads the parts that are still missing.

**Works with ddrescue.** The map file uses GNU ddrescue's format, so you can start a rescue in
DiscForge and finish it with ddrescue (or the other way round).

**New commands:** `dforge rescue` and `dforge rescue-status`.

When a rescue ends with sectors still missing, DiscForge writes the list next to the image, so the
merge and check tools know exactly where the gaps are.

DiscForge still only copies discs that aren't copy-protected.
