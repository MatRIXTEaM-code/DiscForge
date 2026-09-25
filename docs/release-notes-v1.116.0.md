# DiscForge 1.116.0

Released 25 September 2026

## New

**Open old ACE archives.** DiscForge can now open .ace files made by WinAce and the DOS ACE archiver,
which a lot of 1990s and early-2000s game releases, patches, demos and magazine cover discs were
packed with. WinAce hasn't been available for years and its last unpacker had a serious security
hole, so these files have been hard to open safely.

- Drop an .ace file on the **Extract** tile to see what's in it, then extract one file or all of
  them. "Extract all" keeps the archive's folders.
- Split archives (.ace, .c00, .c01 …) work. Open any part and DiscForge finds the others, as long as
  they're in the same folder. If a part is missing, DiscForge tells you which one.
- Self-extracting .exe archives open too, without running them.
- Password-protected archives ask for the password.
- Archive comments are shown when you open the file.
- New commands: `dforge ace-list`, `dforge ace-test` and `dforge ace-extract`.

DiscForge only reads ACE files. It can't make new ones.

**Safe to open files from anywhere.** Every file name inside an archive is cleaned before anything is
written, so an archive can't put files outside the folder you choose. This is the problem that
affected WinRAR's ACE support in 2019. Files are only saved once their checksum has been confirmed,
so a damaged archive never leaves a half-written file behind.

**Identify knows ACE files.** The Identify screen and `dforge identify` now name ACE archives.

## Thanks

The ACE decoding in DiscForge is based on acefile by Daniel Roethlisberger, used under its BSD licence.
