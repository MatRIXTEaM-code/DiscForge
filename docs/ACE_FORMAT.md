# ACE archives (WinAce) — how DiscForge reads them

DiscForge can list, test and extract ACE 1.0 and 2.0 archives. It cannot create them, and never will:
ACE was designed by Marcel Lemke (e-merge GmbH) and was always closed; the aim here is only to get
old files back out.

## What is supported

- ACE 1.0 archives (LZ77) and ACE 2.0 archives (the "blocked" method with its LZ77, LZ77 + DELTA,
  LZ77 + EXE, SOUND 8/16/32-bit and PIC sub-modes), plus stored files.
- Solid archives (each file continues the previous one's dictionary; files are decoded in order).
- Multi-volume sets: `name.ace`, `name.c00`, `name.c01` … Open any volume; DiscForge starts from the
  `.ace` file when it is present and picks up the rest by name.
- Self-extracting archives (`.exe`): the archive is found within the first 512 KB of the file.
- Password-protected files (ACE's Blowfish), when you supply the password.
- Archive and file comments (shown by `ace-list` and in the Extract tile).
- File names in UTF-8 or the DOS code page 437 that WinAce and DOS ACE used.

Not supported: recovery records (skipped — they are only needed to repair a damaged archive), NTFS
permissions (skipped), and anything beyond ACE 2.0.

## Safety

Every stored name is cleaned before anything is written: both kinds of slash split folders; drive
letters, colons, control and wildcard characters, `.` and `..` parts and leading slashes are removed;
Windows device names (`CON`, `NUL`, `COM1` …) get a leading underscore; trailing dots and spaces are
trimmed. The final path is then checked to be inside the chosen folder, and DiscForge will not write
through a link or junction that already exists there. This covers the WinAce/UNACEV2.DLL path
traversal that was exploited in 2019 (CVE-2018-20250). Each file is written to a `.dfpart` temporary
file and only renamed into place once its CRC-32 has checked out.

## Where the knowledge comes from

The container (headers, flags, volumes, file records) follows Marcel Lemke's public "Technical
information of the archiver ACE v1.2" (1998). The compression algorithms were never published. DiscForge's
decoder is a C# port of **acefile** by Daniel Roethlisberger (https://github.com/droe/acefile), a
pure-Python ACE decompressor written without the original source and checked against WinAce 2.69 and
unace 2.5. acefile is used under its BSD 2-clause licence; the notice is reproduced in `NOTICE` and at
the top of `src/DiscForge.Core/Ace/AceCodec.cs`. No WinAce, UNACEV2.DLL or unace code or binary was
used, run or examined.

## How it was tested

`tests/fixtures/ace/` holds small archives built by `make-fixtures.py` without any ACE compressor:

- `winace-solid.ace` — the first 35 entries of a real WinAce 2.0 solid archive (from acefile's test
  data; BSD-licensed contents), with folders, stored and compressed files and an archive comment;
- `winace-solid-password.ace` — the same with every file encrypted (password `DiscForge`);
- `winace-multi.ace/.c00/.c01` — the same split over three volumes, with files cut across volumes;
- `synthetic-modes.ace` — hand-built bit streams for ACE 1.0 LZ77, all four SOUND modes and PIC;
- `hostile-names.ace` — path-traversal and device names;
- `cp437-cafe.ace` — a DOS code-page file name.

Every file in them is compared with the output of acefile (`manifest.txt`). The full 5.6 MB WinAce
archive from acefile's test data (268 entries, LZ77 + DELTA + EXE) also extracts byte-identically; that
test runs when `ACE_TESTDATA` points to a copy of the test data.
