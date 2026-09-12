// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.App.Views;

/// <summary>
/// The in-app manual: one entry per launcher tile, with a short line and a longer
/// "what it does / how to use it" write-up. Kept here as plain data so the Help tile
/// stays a single source of truth — when a tile is added, add its entry here too.
/// </summary>
internal static class HelpContent
{
    internal sealed record Entry(string Key, string Glyph, string Title, string Summary, string Body);

    public static readonly IReadOnlyList<Entry> Entries = new Entry[]
    {
        new("record", "🔥", "Record Disc", "Write an image to a recorder",
            "Burns a prepared image to a blank CD/DVD/Blu-ray in a detected recorder. Choose the image, " +
            "pick the drive and write speed, and DiscForge writes it via IMAPI. Use the Drives tile first to " +
            "confirm the recorder and its supported speeds."),

        new("copy", "⇄", "Copy Disc", "Duplicate a disc",
            "Reads a source disc to a temporary image and writes it back out to a blank — a straight " +
            "disc-to-disc duplicate. For a faithful copy of a protected or mixed-mode disc, read with the " +
            "Read Disc / Raw Lab tiles first and inspect the result."),

        new("read", "📀", "Read Disc", "Rip a disc to an image",
            "Rips a physical disc in a drive to an image file on disk (BIN/CUE or ISO), sector by sector. " +
            "This is the starting point for preserving a disc: rip here, then verify, convert or submit it " +
            "with the other tiles."),

        new("create", "💿", "Create Image", "Build an image from files",
            "Builds a new optical image from a folder of files — choosing the disc type, filesystem " +
            "(ISO 9660 with Joliet/Rock Ridge, or UDF) and boot record (El Torito) as needed. Point it at a " +
            "folder and it lays out a mountable, burnable image."),

        new("rawlab", "⚙", "Raw Lab", "Compose / analyse raw DAO",
            "The low-level workshop: assemble or dissect a raw disc-at-once image with full 2352-byte " +
            "sectors and sub-channel. Use it when you need byte-exact control over sectors, modes and the " +
            "lead-in/lead-out rather than a filesystem-level view."),

        new("inspect", "🔍", "Inspect", "Read and verify a CDI image",
            "Opens a CDI image and shows its version, sessions and full track layout, and verifies its " +
            "structure. The dedicated viewer for the native CDI container (the Examine tile is the " +
            "everything-else inspector)."),

        new("sectors", "▦", "Sector Viewer", "Annotated hex of any sector",
            "Shows any sector of an image as annotated hex — sync, header, mode, user data and EDC/ECC " +
            "fields called out. The tool for understanding exactly what a sector contains or why one is " +
            "malformed."),

        new("tools", "🧰", "Tools", "Checksums, split / join",
            "General utilities: compute CRC-32/MD5/SHA-1/SHA-256 of a file, and split a large image into " +
            "parts or join split parts back together. Handy for checking a download or preparing an image " +
            "for transfer."),

        new("drives", "🖥", "Drives", "Detected recorders",
            "Lists the optical drives DiscForge found and interrogates each for its real capabilities — " +
            "read/write speeds, supported media, features. Check here first before recording or ripping."),

        new("protect", "🛡", "Protection", "Scan for copy-protection",
            "Scans a disc or image for copy-protection fingerprints (LibCrypt sub-channel patterns, sector " +
            "anomalies and similar) and reports what it detects. DiscForge detects and preserves protection " +
            "faithfully; it never circumvents it."),

        new("dvdshrink", "🎬", "DVD Shrink", "Shrink DVD-Video to fit",
            "Plans and performs a re-author of a DVD-Video so it fits a smaller disc, re-writing the " +
            "structural IFOs. Menu authoring is left to dedicated tools by design; DiscForge handles the " +
            "structure and the shrink plan."),

        new("accuraterip", "🎵", "AccurateRip", "Verify an audio rip",
            "Checks an audio-CD rip against the AccurateRip database: it computes the same track checksums " +
            "and tells you whether your rip matches other verified rips of the same pressing — proof the " +
            "audio was read correctly."),

        new("mount", "💾", "Mount", "Mount an image as a drive",
            "Mounts an image so its contents appear as a drive letter, without burning it. Good for pulling " +
            "a few files out of an image or running its contents directly."),

        new("interop", "🔗", "CloneCD", "Read / write CloneCD .ccd",
            "Reads and writes the CloneCD triplet (.ccd control file, .img data, .sub sub-channel), so " +
            "images move between DiscForge and CloneCD-based workflows without losing sub-channel data."),

        new("recovery", "🩹", "Recovery", "Recover damaged sectors",
            "Re-reads a scratched or failing disc, using the drive's C2 error pointers to find and retry the " +
            "bad sectors until they read cleanly or a retry cap is hit. Aimed at rescuing data from marginal " +
            "media."),

        new("merge", "🧩", "Merge Rips", "Combine several rips into one",
            "Merges two or more imperfect rips of the SAME disc into one best-possible image. Sectors the " +
            "copies agree on are kept; any copy whose EDC validates is used as-is; the rest are majority-voted " +
            "and re-checked, so a sector no single copy had whole can be reassembled from several. Unrecoverable " +
            "sectors are reported. The multi-read companion to Recovery."),

        new("mergecert", "🔏", "Merge + Certify", "Merge rips with a signed provenance certificate",
            "The same multi-read merge as Merge Rips, plus a checkable, optionally-signed certificate " +
            "recording exactly how every sector was decided (agreement / EDC / vote / single-source) and " +
            "which copy supplied it. Also honours a \"<source>.badsectors.json\" sidecar next to each rip, " +
            "excluding its known-unreadable sectors from the vote instead of counting a zero-filled read as " +
            "evidence. Use this over plain Merge Rips whenever the reconstruction needs to be auditable."),

        new("dumpcert", "📜", "Dump Certificate", "Certify or verify a single dump",
            "Records a signed, machine-readable account of ONE dump event: the image's SHA-256, a Merkle " +
            "root over every sector (so any single sector can later be proven byte-identical without " +
            "rehashing the whole image), and the drive/settings/firmware context. Unlike Merge + Certify " +
            "(several rips reconciled into one), this certifies a single already-finished image as-is. " +
            "Verify re-checks a certificate's signature and, if the image is still alongside it, its hash " +
            "and Merkle root too."),

        new("prove", "🔥", "Prove", "Burn, read back, and verify byte-for-byte — one verdict",
            "The round trip, one verb, one verdict. Burns a .cue to the chosen drive via the direct-SPTI " +
            "RAW DAO-96 engine, then reads every track back off the disc and compares it byte-for-byte " +
            "against the exact image that was burned — main channel, EDC/ECC, and every Q sub-channel " +
            "frame, not just an MD5. Prints ONE final verdict: PROVEN or FAILED. This is a REAL burn, not " +
            "a simulation — it consumes a blank disc and cannot be undone once started. Proves the burn " +
            "itself, not the whole preservation chain end to end."),

        new("pressingdna", "🧬", "Pressing DNA", "Fingerprint a pressing offline; compare two",
            "Fingerprints a PRESSING from a .cue: exact track geometry and pregap lengths, where the " +
            "audio actually sits inside each track (write-offset artifacts), and the cue's MCN/ISRC " +
            "identity — the offline cousin of reading the ring code. With one cue, shows that pressing's " +
            "own fingerprint. With two, says SAME PRESSING (every trait agrees) / same title but a " +
            "DIFFERENT PRESSING (each differing trait named, including the constant-shift write-offset " +
            "signature) / different discs entirely. A pure local-file analysis — no live drive."),

        new("drivedossier", "📋", "Drive Dossier", "Per-drive memory: quirks accumulate into warnings",
            "The institutional memory a session's terminal scrollback used to eat: a per-drive dossier " +
            "that accumulates observed behaviour across every operation on THIS physical drive — mute " +
            "signatures (audio-as-data reads that zero-fill but report success), C2 pointers crying " +
            "wolf on the opening sector, an offset a real AccurateRip confirmation pinned, how deep its " +
            "overread reaches. Distils into warnings the next dump can see before it repeats a hard " +
            "lesson. Detect a drive (or type its vendor/model) to load or start its dossier, then " +
            "optionally add an observation by hand. Stored as local JSON under %AppData%\\DiscForge\\" +
            "drives by default — the community reference data (drive-db) is separate and fixed; this " +
            "is what THIS drive, on THIS bench, actually did."),

        new("discactuary", "⏳", "Disc Actuary", "How long a disc has left, from its scan history",
            "A quality scan says how a disc is TODAY; the actuary keeps every scan as a time series and " +
            "fits a first-order decay model per disc, so it can say how long it has LEFT — and, across " +
            "a whole collection, which discs to re-dump first because they're dying fastest. Record a " +
            "scan by typing its tier1/tier2/uncorrectable maxima (CD: C1/C2/CU, DVD: PIE/PIF/POF) or by " +
            "importing a scan file (Nero DiscSpeed, opti-drive, csv, and more). A trend needs 3+ scans " +
            "on record — fewer than that, it says so rather than guessing. Optionally condition the fit " +
            "on the shelf's storage temperature/humidity. Prioritisation only: it schedules rescues, it " +
            "performs none. Local JSON under %AppData%\\DiscForge\\actuary by default, one file per disc."),

        new("discmri", "🩻", "Disc MRI", "Polar damage map on the physical disc",
            "Renders per-sector evidence as a polar map of the PHYSICAL disc (real Red Book spiral " +
            "geometry), so damage shows its true shape: a radial streak is a scratch, a ring is a " +
            "pressing defect, a bloom from the hub is rot, a solid outer band is a muted/failed read " +
            "region. Open a raw image (.bin) or a single-file .cue (a cue supplies per-track audio/data " +
            "knowledge, so sync-less sectors aren't ambiguous); a dump's .badsectors.json sidecar is " +
            "auto-detected next to the image, or pick one explicitly. Worst evidence wins per pixel — " +
            "damage never hides. Save As writes either a .svg (map + legend) or a bare .png, matching " +
            "dforge disc-mri's own two output modes. Plan Re-read turns the same evidence into a " +
            "targeted, escalating re-read plan (coalesced ranges + suggested pass count) — offline, the " +
            "same as dforge disc-mri --plan-reread; Save Plan writes the JSON that dforge disc-mri-reread " +
            "then drives through a real drive's Tier-B adaptive re-read controller (a separate, hardware-" +
            "facing CLI step, not run from this tile)."),

        new("secureripplan", "🗺", "Secure-Rip Plan", "Grade rip evidence and plan re-reads",
            "Grades a rip's per-sector evidence (clean / C2-flagged / pass-mismatch / unreadable, across " +
            "however many passes were made) into VERIFIED / CONSISTENT / SUSPECT / FAILED per track — " +
            "VERIFIED only when an independent AccurateRip match corroborates it, CONSISTENT being the " +
            "honest ceiling from self-agreement alone — and plans exactly which sector ranges still need a " +
            "targeted re-read. The EAC-style secure-rip depth, done offline against a saved evidence file."),

        new("dumpledger", "🧾", "Dump Ledger", "A public log of independently signed dump claims",
            "A public, hash-chained log of independently signed claims — \"this disc dumps to these exact " +
            "bytes\" — so strangers can see for themselves how many independent submitters agree, without " +
            "trusting DiscForge or any single submitter. Open or start a ledger, Verify checks the whole " +
            "chain is intact and every entry's signature genuinely matches the key it names, and Consensus " +
            "groups every submission for one disc fingerprint by DISTINCT submitter key (never raw " +
            "submission count), so a lone re-submitter can never look like agreement. Generate a key once " +
            "(keep the private key file secret — it's what proves a submission is really yours), then " +
            "Submit appends a new, independently signed claim. Pure local-file analysis and offline ECDSA " +
            "signing — no live drive, and no central server: the ledger file itself is the whole trust " +
            "mechanism."),

        new("mediamortality", "📉", "Media Decay", "Federated model of how fast a cohort of discs decays",
            "A community mortality model for optical media that pools statistics across independent " +
            "collections WITHOUT centralizing anyone's raw disc data. Observe folds in one disc's own " +
            "Disc Actuary rot-kinetics fit (growth %/yr and sample count) as two numbers, nothing " +
            "identifying — no disc id, title, or scan history ever appears in the model file. Merge With " +
            "combines any two contributors' models EXACTLY (the same mathematics as computing one model " +
            "centrally would give, order-independent, no coordinator needed), and Estimate reports a " +
            "cohort's mean decay rate only once 3+ independent discs back it — a privacy floor against " +
            "re-identifying a single contributor's disc, not an accuracy safeguard. Show All lists every " +
            "cohort currently in the model, flagging which ones are still below that floor."),

        new("vobdemux", "✂", "VOB Demux", "Split a VOB/MPG into streams",
            "Splits an unencrypted MPEG program stream (a VOB or MPG) into its elementary video, audio and " +
            "DVD private (AC3/DTS/LPCM/subpicture) streams. A CSS-scrambled VOB stays scrambled — DiscForge " +
            "parses the container but never decrypts it."),

        new("vcd", "📼", "Video CD", "Write VCD control sectors",
            "Writes the two Video CD control sectors — INFO.VCD (album/disc identity) and ENTRIES.VCD (the " +
            "play-point list) — into a VCD/ folder. Assembling a full player-verified VCD image is a separate, " +
            "sample-gated step and is not done here."),

        new("ifoedit", "🗺", "IFO Editor", "Edit and rebuild DVD IFOs",
            "Dumps a DVD-Video disc's structure to editable JSON — chapters, angles, audio and subtitle " +
            "languages — lets you edit it, and rebuilds the VIDEO_TS IFO files. IFO files are unencrypted even " +
            "on a CSS disc, so this never touches scrambled video."),

        new("quality", "📊", "Disc Quality", "Measure surface errors",
            "Runs a surface scan and reports error rates across the disc — a health check that shows whether " +
            "a burn is good or a disc is degrading, and where the trouble spots are."),

        new("browse", "📁", "Browse Files", "List and extract files",
            "Opens an image's filesystem and lets you browse and extract individual files, without mounting " +
            "or burning it. Works across the filesystems DiscForge reads (ISO 9660, Joliet, Rock Ridge, UDF)."),

        new("ripaudio", "🎧", "Rip Audio", "Rip an audio CD to WAV",
            "Rips an audio CD track by track to WAV, correcting read jitter and verifying against " +
            "AccurateRip as it goes — a clean, verified audio rip rather than a raw grab."),

        new("cue", "📝", "Cue Editor", "Check and repair a cuesheet",
            "Validates a .cue sheet against its BIN data and flags or repairs common problems — wrong track " +
            "types, bad indexes, mismatched file references. Use it when a bin/cue won't load correctly."),

        new("subcode", "〰", "Sub-channel", "Analyse Q sub-channel",
            "Analyses the Q sub-channel of a disc or image and fingerprints LibCrypt-style protection. Shows " +
            "the sub-channel structure and where deliberately-corrupt Q frames (a protection signature) sit."),

        new("dvdinfo", "🎬", "DVD Structure", "Titles, chapters, streams",
            "Reads a DVD-Video's structure: titles, chapters, and the audio and subtitle streams in each. A " +
            "read-only view for understanding what a DVD contains before shrinking or re-authoring it."),

        new("pack", "📦", "Pack Discs", "Fit files across discs",
            "Given a pile of files and a target disc size, works out how to distribute them across the fewest " +
            "discs with the least wasted space — a bin-packing planner for burning a large collection."),

        new("transcode", "🎞", "Shrink Video", "Re-encode video to fit",
            "Re-encodes a video to hit a target file or disc size, trading bitrate for size. Pairs with DVD " +
            "Shrink when the structural shrink alone isn't enough."),

        new("patch", "🩹", "PPF Patch", "Apply or build a PPF/IPS/BPS patch",
            "Applies a patch to an image or ROM, or builds a patch from an original and a modified copy. " +
            "Supports PlayStation PPF (v1–v3) plus IPS and BPS. Drop in the file and the patch, or two files " +
            "to diff."),

        new("dreamcast", "🎮", "Dreamcast", "Browse / extract / convert a GD-ROM",
            "Works with Dreamcast GD-ROM images (.gdi): browse the filesystem, extract files, read the " +
            "IP.BIN boot header and convert between layouts. The hub for Dreamcast disc work."),

        new("xbox", "🟢", "Xbox", "Browse / extract / build an XISO",
            "Reads the Xbox XDVDFS filesystem: browse and extract files from an Xbox disc image, and build a " +
            "new XISO from a folder."),

        new("udfcreate", "🗂", "UDF Image", "Build a UDF 1.02 image",
            "Builds a UDF 1.02 image from a folder — the filesystem used by DVDs and larger data discs. " +
            "Point it at a folder and it produces a mountable UDF image."),

        new("identify", "🔎", "Identify File", "Say what any file is",
            "Drop in any file and DiscForge names its format — disc image, ROM, floppy, save, audio and so " +
            "on. The quick 'what is this?' check; the Examine tile then shows the parsed detail."),

        new("examine", "🔬", "Examine", "Identify and show parsed detail",
            "Identifies a file and then shows its parsed contents: a ROM's header and hashes, a disk image's " +
            "partition table, a floppy or CD-i directory, a memory card's save list, an audio file's tags. " +
            "One inspector over every format DiscForge reads."),

        new("library", "📚", "Library", "Scan, verify and rename a collection",
            "Points at a whole folder tree and identifies, hashes and (against a Redump/No-Intro DAT) " +
            "verifies every file, then reports what's confirmed-good, mis-named, duplicated, unknown or " +
            "missing — and can rename verified files to their canonical DAT names."),

        new("convert", "🔀", "Convert", "Any image format to any other",
            "Reads a disc image into a canonical model and writes it back out in another format — BIN/CUE, " +
            "CHD, ISO, CDI or NRG in, any of them out. Pick an input and an output name; DiscForge does the " +
            "round-trip."),

        new("submit", "📤", "Submit", "redump.org submission info",
            "Generates redump.org-style submission info for a dump: per-track and whole-image CRC-32/MD5/" +
            "SHA-1, sizes, the cuesheet and a LibCrypt/sub-channel summary, with the physical fields left " +
            "for you to fill in."),

        new("extract", "🗃", "Extract", "Pull files/saves out of a container",
            "Opens a WBFS container, a floppy image (D64/ADF/FAT12), a memory card (PS1 .mcr, GameCube card, " +
            "Dreamcast VMU) or a PSP EBOOT.PBP, lists what's inside, and extracts one item or all of them. " +
            "The write-side companion to Examine."),

        new("cheat", "🎯", "Cheat Codes", "Decode / encode cheat codes",
            "Decodes a Game Genie (NES/SNES/Genesis/Game Boy) or GameShark (PS1) code into its raw address " +
            "and value, and encodes an address+value back into a Game Genie code. Format translation only — " +
            "it reads and writes the published encodings."),

        new("media", "🔊", "Game Media", "Decode ADX→WAV, render CD+G→PNG",
            "Turns game media into something a PC can open: a CRI ADX ADPCM stream into a 16-bit WAV, and a " +
            "CD+G graphics stream into a PNG frame (with a live preview). Pick a file and decode or render it."),

        new("playlists", "📋", "Playlists", "Export front-end library files",
            "Turns a folder into the library files front-ends read: a RetroArch .lpl playlist or an " +
            "EmulationStation/RetroBat gamelist.xml (scan-and-export), and a multi-disc .m3u you assemble by " +
            "hand with disc ordering."),

        new("sets", "🗂", "Sets", "1G1R filter and rebuild a set",
            "Two collection tools: build a 1G1R ('one game, one ROM') subset of a DAT by region priority and " +
            "save it as a filtered DAT, and rebuild a messy folder into a clean, canonically-named set with a " +
            "missing/unknown report."),

        new("datbuild", "🏷", "DAT Build", "Hash a folder into a Redump-style DAT",
            "Hashes every file in a folder and writes a Redump-style DAT — the reference file other " +
            "tools (rebuild, dat-verify, 1G1R, library scan) check dumps against. For turning a " +
            "collection you already trust into a DAT other tools can verify against, when no public " +
            "one exists yet or you're cataloguing something bespoke."),

        new("memcard", "💳", "Memory Cards", "Read console saves",
            "Reads and lists the saves on a PlayStation 1 (.mcr), PlayStation 2 (.ps2) or Dreamcast VMU " +
            "memory-card image. For extracting individual saves out to files, use the Extract tile."),

        new("psxasset", "🎨", "PSX Assets", "TIM/VAG/TMD/PS-EXE",
            "Pulls PlayStation assets into standard formats: TIM images to PNG, VAG audio to WAV, TMD models " +
            "to DXF, and reads PS-EXE executables. For working with the media inside a PS1 game."),

        new("textures", "🖼", "Textures", "Decode GameCube/Wii TPL textures",
            "Opens a GameCube/Wii TPL texture archive, lists every texture inside (size, GX pixel " +
            "format), and decodes any of them — or all of them at once — to PNG. For pulling art " +
            "assets out of a GameCube/Wii disc's files for reference or reuse."),

        new("compimg", "🗜", "Compressed", "CSO/ZSO ↔ ISO, identify CHD",
            "Compresses an ISO to CSO/ZSO and decompresses it back, and identifies CHD images and their " +
            "metadata. For the compressed containers emulators use to save space."),

        new("bincue", "🧩", "Bin/Cue", "Merge or split bin/cue",
            "Merges a multi-file (per-track) bin/cue into a single BIN with one cue, or splits a single BIN " +
            "back into per-track files. For normalising bin/cue layouts between tools."),

        new("psxbuild", "🛠", "PSX Build", "Build a Mode 2 bin/cue",
            "Builds a Mode 2/2352 bin/cue image from a folder — the raw sector layout a PlayStation disc " +
            "uses. For authoring a PS1-style data disc from files."),

        new("scummvm", "🕹", "ScummVM", "Fingerprint or export for ScummVM",
            "Fingerprints a classic adventure game so ScummVM can identify it, and exports a disc's contents " +
            "into the folder layout ScummVM expects."),

        new("milcd", "💿", "MIL-CD → CDI", "Convert a MIL-CD to a two-session CDI",
            "Converts a Dreamcast MIL-CD bin/cue into a two-session CDI image. A faithful container " +
            "conversion — it repackages the data, it doesn't defeat any console security."),

        new("dcid", "🔷", "Identify DC", "Read a Dreamcast boot header",
            "Reads a Dreamcast disc's IP.BIN boot header and reports the title, product number, region and " +
            "release info. A quick identity check for a Dreamcast image."),

        new("floppy", "💾", "Floppy", "Image a floppy, or launch flux-capture hardware's own tool",
            "DiscForge images an ordinary floppy from a standard drive itself (the CLI's floppy-image " +
            "command), and already reads a KryoFlux or SuperCard Pro flux file once one exists. Capturing " +
            "that flux from real KryoFlux or Greaseweazle hardware over USB needs each board's own vendor " +
            "software, so this tile is an escape hatch that launches KryoFlux's DTC or Greaseweazle's gw " +
            "client — DiscForge doesn't bundle either or reimplement a USB driver for them."),

        new("psp", "🎮", "PSP", "Launch UMDGen to edit a PSP ISO",
            "DiscForge already reads a PSP UMD's filesystem and PARAM.SFO without decrypting anything, but " +
            "has no ISO editor of its own — a physical UMD is dumped by homebrew running on the PSP itself, " +
            "not by anything a PC talks to, so this tile is for editing an image you already have rather " +
            "than acquiring one. Launches UMDGen; DiscForge doesn't bundle or inspect it."),

        new("cart", "🕹", "Cartridges", "Launch GBxCart RW/FlashGBX or Cart Reader to dump a cartridge",
            "DiscForge reads N64, SNES, Genesis, GB/GBC, GBA and NES ROM dumps once you have them, but has " +
            "no cartridge-reading hardware of its own — that's always a flashcart's job. There's no single " +
            "dominant tool the way there is for optical discs, so this tile offers both major community " +
            "options: GBxCart RW/FlashGBX for the Game Boy family, Cart Reader for N64/SNES/Genesis/NES."),

        new("formatmedia", "💳", "Format Media", "Launch a card formatter to prep an SD/SDHC/SDXC card",
            "DiscForge has no code that talks to a card reader/writer, and formatting removable media " +
            "correctly (the partition table and filesystem a card's own controller expects) is its own " +
            "established problem, not something to reimplement here. Launches a dedicated formatter — " +
            "e.g. the SD Association's official SD Card Formatter — for prepping a card before using " +
            "it with a flashcart-based dumper (see Cartridges) or a floppy-imaging rig. DiscForge " +
            "doesn't bundle or inspect the formatter; double-check the drive letter it shows before " +
            "formatting, since this erases the whole card."),

        new("rawcopy", "🗄", "Raw Copy", "Launch a sector-level drive/image cloning tool",
            "DiscForge's own sector-level code is built around CD/DVD/BD structure, not generic " +
            "block-device I/O — whole-drive or whole-image byte-for-byte cloning (HDD/SSD/USB, or an " +
            "existing raw image, in either direction) is a different, already-solved problem. Launches " +
            "a dedicated tool — e.g. HDD Raw Copy Tool — for that. DiscForge doesn't bundle or inspect " +
            "it; triple-check source and target there before starting, since a raw copy overwrites the " +
            "target completely and cannot be undone."),

        new("verify", "✓", "Verify & Lint", "Structural and filesystem conformance checks",
            "Runs the right structural check for whatever image you give it: ISO 9660/UDF/FAT/HFS " +
            "conformance, a filesystem cross-check against the raw sectors, CHD internal integrity, " +
            "or a PlayStation 2 memory-card's per-page ECC. For catching a structurally broken image " +
            "before it causes confusing failures somewhere else."),

        new("settings", "🛠", "Settings", "Preferences and diagnostics",
            "Application preferences and diagnostic options, including where DiscForge writes its log. Open " +
            "the log folder from here or from About if you need to report an issue."),

        new("about", "ℹ", "About", "Version, licence and diagnostics",
            "Shows the version, what DiscForge is, and the licence, and opens the log folder for diagnostics. " +
            "The first place to look for the version number when reporting anything."),

        new("help", "📖", "Help", "What each tile does and how to use it",
            "This manual: a searchable list of every tile with a description of what it does and how to use " +
            "it. Type in the search box to filter by name or text, and pick a tile to read its entry."),

        new("exit", "✕", "Exit", "Close DiscForge",
            "Closes the application. Any task windows you have open are closed with it."),
    };
}
