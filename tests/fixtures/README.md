# Test fixtures

## cdi4dc_audiodata_v35.cdi + source.iso

A **real** CDI image, not a synthetic mock. `source.iso` was built with
genisoimage from known-contents files; `cdi4dc_audiodata_v35.cdi` is that ISO
run through cdi4dc 0.3b (SiZiOUS) as an Audio/Data image.

Properties (all confirmed by docs/reference/validate_cdi.py):
- Format: CDI v3.5 (magic 0x80000006)
- Sessions: 2 (audio, then data)
- Data track: Mode 2 / Form 1, 2336-byte sectors, user data at +8
- Known payload strings: "DiscForge CDI parser validation", "0123456789ABCDEF"
- Volume name: OJTEST

Use this as the golden image for parser + extractor round-trip tests. The
eventual C# test should: parse → locate the data track → extract user data →
reconstruct the ISO → byte-compare against source.iso.

## Wanted: a genuine DiscJuggler-authored image

cdi4dc images are simpler than what DiscJuggler itself wrote. A real DJ .cdi
(especially multi-track, or a v2/v3) is the next validation priority.


## The old name inside these files

`source.iso` and the synthetic images contain the string `OPENJUGGLER` — the
project's former name, stamped into ISO 9660 publisher/preparer fields when they
were built.

**Do not "fix" this.** These fixtures are historical artifacts whose exact bytes
are asserted by CRC-32 tests. Rewriting the string would change their checksums
and break the tests that prove the parser reads real images correctly.

The builder now stamps `DISCFORGE`; that only affects newly created images.

## xdelta/ — real xdelta3 patches for VcdiffPatchTests

Made with xdelta3 3.0.11 (Linux). `src.bin`/`tgt.bin` are not stored: the test regenerates them
from a fixed LCG (see `VcdiffPatchTests`), SHA-1 of the target `1bfdd91a…cef0`.

- `none.xd`  — `xdelta3 -e -S none -s src.bin tgt.bin` (header names LZMA; nothing compressed)
- `lzma.xd`  — `xdelta3 -e -S lzma -s src.bin tgt.bin` (sections as .xz; stored LZMA2 chunks)
- `multi.xd` — `xdelta3 -e -S lzma -W 16384 -s src.bin tgt.bin` (13 windows; the .xz streams
  continue across windows)
- `nosrc.xd` — `xdelta3 -e -S lzma tgt2.bin` where tgt2 = "DiscForge xdelta fixture " repeated to
  100,000 bytes (no source file)
- `words-lzma.xd` — `xdelta3 -e -S lzma tgt3.bin`, tgt3 = 6,000 random words (Python
  `random.seed(7)`), SHA-1 `11f9de8b…eb6d`; genuinely LZMA-compressed chunks
- `djw.xd` — same input with `-S djw`: DJW secondary compression, which DiscForge declines
