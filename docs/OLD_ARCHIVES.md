# Old archives: ACE, LHA/LZH, ARJ and ZOO

DiscForge reads (never writes) the archive formats that 1990s DOS, Amiga and early Windows software,
shareware, demos, patches and cover discs were distributed in. ACE has its own page
([ACE_FORMAT.md](ACE_FORMAT.md)); this page covers the others and the features shared by all four.

## Formats

| Format | Methods read | Also |
|---|---|---|
| **LHA / LZH** (LHarc, LHA, LHa for UNIX, Amiga LhA, UNLHA32, LHARK) | -lh0- -lh1- -lh4- -lh5- -lh6- -lh7- -lhx- and LHARK's own -lh7- | header levels 0–3, 64-bit sizes, folders, Amiga and OS-9 quirks, self-extracting .exe/.com/.run |
| **LArc** (.lzs) | -lz4- -lz5- -lzs- | |
| **ARJ** | 0 (stored), 1–3, 4 (fastest) | multi-volume (.arj, .a01, .a02 …), self-extracting .exe, "garbled" password files |
| **ZOO** | 0 (stored), 1 (LZW), 2 (LZH) | long names and folders, deleted entries skipped |

Not supported: LHA -lh2-/-lh3- (two experimental LHA 2.0x methods that almost nothing used), PMarc
(.pma), ARJ-SECURITY envelopes and ARJ's later GOST encryption. Symbolic links stored in Unix LHA
archives are skipped (listed as a warning). MacLHA files keep their MacBinary wrapper.

Every file is checked against the archive's CRC (CRC-16 for LHA and ZOO, CRC-32 for ARJ) before it is
kept, and extraction follows the same safety rules as ACE: names are cleaned, nothing can be written
outside the chosen folder or through an existing link, and files go through a `.dfpart` temporary file.

## Where to find it

- **Extract tile:** drop an archive (or any volume of a set, or a self-extracting .exe).
- **Extract tile, disc image:** drop an ISO, bin/cue or CDI to list the old archives stored on it and
  unpack them straight off the image.
- **Extract tile, "Check a folder…":** tests every old archive under a folder (sets counted once,
  self-extracting .exe files included) and marks each OK, damaged, missing a volume or needing a
  password; save the result as CSV, or extract the good ones with their folders.
- **Explorer (optional installer tasks):** "Extract with DiscForge" and "Open in DiscForge" on the
  right-click menu of .ace, .lzh, .lha, .arj and .zoo files, and optionally making DiscForge their
  default program.
- **Command line:** `unpack-list`, `unpack-test`, `unpack`, `unpack-sweep`, `unpack-image`
  (see [COMMANDS.md](COMMANDS.md)).

## Where the knowledge comes from

- **LHA/LArc:** the static-Huffman coder behind -lh4- to -lh7- is Haruhiko Okumura's published "ar002"
  design and -lh1- is Okumura and Haruyasu Yoshizaki's LZHUF design, both written here from their
  public descriptions. The LArc methods, the LHARK variant, the header-level details and the tool
  quirks follow **Lhasa** by Simon Howard (ISC licence; notice in `NOTICE` and in
  `src/DiscForge.Core/OldArchives/Codecs.cs`).
- **ARJ:** "ARJ TECHNICAL INFORMATION" (Robert Jung, 1993) for the container; methods 1–3 are the same
  ar002-style coder; method 4 and the garble scheme were confirmed against archives made by ARJ 3.10.
- **ZOO:** Rahul Dhesi's published description of the zoo format; LZW and LZH checked against zoo 2.10.

No archiver's code or binary was disassembled or copied.

## How it was tested

`tests/fixtures/oldarc/` (built by `make-fixtures.sh`) holds LHA and LArc archives made by the
original DOS, Amiga, OS-9 and Unix tools (from Lhasa's test corpus) and ARJ and ZOO archives made
with ARJ 3.10 and zoo 2.10. Every file is compared with what Lhasa, ARJ and zoo themselves extract.
Over the whole Lhasa corpus (about 220 archives from 20 different LHA tools) every archive in a
supported method reads correctly, including a 4.7 GB file; that test runs when `LHASA_TESTDATA` points
to a copy of it.
