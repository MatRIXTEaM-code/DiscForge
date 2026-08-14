# DiscForge — End-User Usage & Coverage Review

*An honest assessment of how DiscForge serves an end user: what it covers, how usable it is, and where the friction is. Figures are measured from the current tree, not estimated.*

## The short version

DiscForge is, by coverage, ahead of every comparable tool. The CLI (`dforge`) exposes **296 commands**; it reads essentially every disc image, optical filesystem, console format, memory card, and — as of the latest work — dozens of adjacent formats (virtual disks, non-disc filesystems, more archives/audio/images). It verifies with a rigour nothing else in this space matches (a full ISO/UDF/FAT/HFS lint suite, CHD integrity, PS2 memory-card ECC, AccurateRip), authors images (ISO 9660, UDF, UDF bridge, Xbox XISO, CDI, bootable El Torito), recovers damaged dumps, catalogs collections, and now burns on both Windows (IMAPI2) and macOS (hdiutil). Documentation is genuinely extensive — a README, a CHANGELOG, and roughly sixty topic guides under `docs/`.

The gaps are not about *capability*. They are about **discoverability and reach**: a quarter of the commands are invisible in the built-in help, the command count shown in the docs is stale, the graphical app is Windows-only, and the sheer size of the tool is itself a barrier for a newcomer. None of these are hard to fix, and none detract from what the tool can do — they affect how easily an end user *finds* what it can do.

## Coverage — what an end user can actually do

The functional coverage is comprehensive across the whole preservation lifecycle:

- **Identify** — a single `identify` command names ~130 formats by documented signature, from every disc-image container (ISO, BIN/CUE, CHD, CDI, NRG, MDS, CCD, GDI, CSO/ZSO, WBFS, RVZ, NKit) through virtual disks (VHD, VMDK, VDI, QCOW2, DMG), filesystems (ISO 9660, Joliet, UDF, HFS/HFS+, FAT, NTFS, exFAT, ext, SquashFS), console media, memory cards, ROMs, and common media/archive types.
- **Read / extract** — browse and extract from every optical filesystem, plus console-specific readers for GameCube/Wii, PlayStation 1/2, PSP, Saturn, Sega CD, Dreamcast, 3DO, PC-FX, CD-i, Neo Geo, and Xbox.
- **Verify** — the strongest column. A complete conformance-lint suite for ISO 9660, UDF, FAT and HFS; CHD archival integrity; PS2 memory-card Hamming ECC; AccurateRip for audio; whole-dump auditing; cross-filesystem verification of bridge/hybrid discs.
- **Author** — build ISO 9660 (Joliet/Rock Ridge), UDF, UDF bridge, Xbox XISO, and data/audio CDI images, with El Torito bootable support.
- **Recover** — C2/CIRC error recovery, bad-sector mapping, signed per-sector merge certificates, read-stability analysis.
- **Catalog** — whole-collection scan-and-verify against DAT files, HTML audit dashboards, and a portable JSON/CSV catalog export for keeping an index beside a NAS/cloud backup.
- **Burn** — data discs to CD/DVD/Blu-ray on Windows (IMAPI2) and macOS (hdiutil), with verify.

Roughly **144 commands** offer `--json`, so the tool scripts and integrates well.

The one deliberate non-goal is *transport*: DiscForge does not sync to cloud or NAS. That is correct — it is the librarian (identify, hash, verify, catalog), and a dedicated tool (rclone, restic) moves the bytes, consuming DiscForge's manifests.

## Usability findings, most impactful first

**1. A quarter of the commands are undiscoverable from the built-in help.** Of the 296 dispatch commands, only **229 appear in the `dforge` help output** — **73 are missing entirely**. Among them are genuinely useful, non-obscure commands: `accuraterip`, `chd-info` / `chd-extract` / `chd-create`, `dat-verify`, `mount`, `transcode`, `ps2mc-info` / `ps2mc-extract`, `psxmc-info` / `psxmc-extract`, the whole `vmu-*` family, `sbi-make` / `sbi-info`, and `tim-*`. A new user running `dforge` with no arguments sees three-quarters of the tool. The `search` command mitigates this for someone who already knows a keyword, but it does not help discovery. *Fix: regenerate the help block from the dispatch table so every command is listed; it is mechanical.*

**2. The documented command count is stale.** `docs/COMMANDS.md` states "**214 commands**," but the dispatch table holds **296**. The reference under-counts the tool by a third, which both undersells it and signals the docs have drifted from the code. *Fix: auto-generate the count and the command list from the dispatch table in CI so they cannot drift.*

**3. Naming is mostly consistent, with a few seams.** Verb-noun patterns are good (`*-info`, `*-lint`, `*-extract`, `create-*`). The visible seams are in the PlayStation memory-card commands, which mix prefixes — `psxmc-info`/`psxmc-extract` alongside `ps1card-convert` and `ps2mc-*`. A user has to know three prefixes for one conceptual area. *Fix: pick one prefix per domain and alias the rest.*

**4. The graphical app is Windows-only.** The desktop app (`DiscForge.App`, 58 views) is WinForms on `net8.0-windows`, so Mac and Linux users get the CLI only. The CLI is now cross-platform and even burns on macOS, which softens this, but a non-technical Mac/Linux user has no GUI. This is a large effort to change (a cross-platform UI), so it is a positioning fact more than a quick fix — worth stating plainly rather than pretending the GUI is universal.

**5. The tool's size is its own onboarding barrier.** 296 commands and sixty docs is a strength for a power user and a wall for a newcomer. `COMMANDS.md` is an exhaustive reference, not an on-ramp. There is no short, task-oriented "here are the ten things most people want to do" quickstart. *Fix: a one-page task-first quickstart — "dump a disc," "check a dump is good," "build a bootable ISO," "catalog my collection," "burn an image" — each mapping to one or two commands.*

## What is genuinely missing (small)

Coverage gaps are minor and mostly deliberate. Cloud/NAS sync is out of scope by design. A handful of obscure read formats remain unidentified (very old or proprietary containers), but nothing an end user is likely to meet. On the burn side, Linux has no backend (only Windows and macOS do), so a Linux user can author but not burn from DiscForge itself.

## Recommendations, prioritized

1. **Close the discoverability gap** — regenerate the help block and the `COMMANDS.md` count/list from the dispatch table, so every command is listed and the count is always correct. Highest impact, lowest effort.
2. **Add a task-first quickstart** — a single page mapping the ten most common goals to their commands, linked from the README as the first thing a new user reads.
3. **Unify the memory-card command prefixes** — one prefix per domain, with aliases for the rest.
4. **State the platform story clearly** — CLI everywhere (and it burns on Windows + macOS); GUI on Windows only. Set expectations rather than surprise Mac/Linux users.
5. **Optionally, a Linux burn backend** — `cdrecord`/`wodim`-style, to complete the burn story on the third platform.

## Bottom line

For *capability and correctness*, DiscForge is already ahead of the field and, unusually for this space, everything is validated against independent references. The work that would most improve the **end-user** experience is not more features — it is making the features already present easy to find (help/docs regeneration), easy to start with (a task-first quickstart), and honest about platform reach (CLI-everywhere, GUI-Windows-only). Those are days of work, not months, and they would convert a deep expert tool into one a newcomer can also pick up.
