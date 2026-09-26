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

**Tries harder where it matters.** In the damaged areas DiscForge slows the drive down, which often
gets a disc to read when it won't at full speed. It also makes sure every retry really reads the disc
again rather than getting the same answer from the drive's memory. On CDs, a sector that still won't
read is read several more times in raw form. The good parts of each attempt are pieced together, and
the disc's own error-correction data repairs the rest. The result is only kept if its checksum proves
it's right. Worn areas that read very slowly are left until later, so the good parts of the disc come
off first.

**Last resort.** An option can fill the sectors that never read with the drive's best guess instead of
blanks. They stay marked as bad, because the data may be wrong, but for a video or music file a nearly
right sector can be better than a gap. It's off unless you turn it on.

**Stop and carry on later.** Progress is saved in a small map file next to the image. You can stop at
any time and press Start again later, and it carries on where it left off.

**Try another drive.** Some drives read damaged discs better than others. Put the disc in a different
drive, pick the same image, and press Start. DiscForge only reads the parts that are still missing.

**Works with ddrescue.** The map file uses GNU ddrescue's format, so you can start a rescue in
DiscForge and finish it with ddrescue (or the other way round).

**Protect Image.** Makes a small repair file for a disc image while it's good (about 3 to 14% of the
image's size, your choice). If the image is damaged later, by a failing hard drive, a bad copy, or a
disc that could only be partly rescued, DiscForge finds every damaged sector and rebuilds it. Keep a
copy of the repair file on another drive.

**Six more new screens:**

- **Bit-Rot Watch** remembers every file in a folder and later tells you what's changed. It especially
  flags files whose contents changed although nothing saved to them, which is how silent corruption
  looks.
- **Compare Images** shows what's different between two images, file by file or byte by byte.
- **Checksum Files** makes and checks .sfv, .md5 and .sha1 files, and checks PAR2 sets.
- **Find in Image** searches an image for text or bytes and tells you which file on the disc each match
  is in.
- **Sector Health** draws a map of a raw CD image showing good, repairable and damaged sectors, and
  saves a repaired copy.
- **Best of Dumps** builds one verified image from several dumps of the same CD. Each sector is taken
  from whichever copy can be proved correct.

**New commands:** `dforge rescue` and `dforge rescue-status`.

When a rescue ends with sectors still missing, DiscForge writes the list next to the image, so the
merge and check tools know exactly where the gaps are.

DiscForge still only copies discs that aren't copy-protected.
