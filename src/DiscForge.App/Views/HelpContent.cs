// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

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

        new("memcard", "💳", "Memory Cards", "Read console saves",
            "Reads and lists the saves on a PlayStation 1 (.mcr), PlayStation 2 (.ps2) or Dreamcast VMU " +
            "memory-card image. For extracting individual saves out to files, use the Extract tile."),

        new("psxasset", "🎨", "PSX Assets", "TIM/VAG/TMD/PS-EXE",
            "Pulls PlayStation assets into standard formats: TIM images to PNG, VAG audio to WAV, TMD models " +
            "to DXF, and reads PS-EXE executables. For working with the media inside a PS1 game."),

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
