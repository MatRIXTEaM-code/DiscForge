# DiscForge — command reference

*Auto-generated from the CLI's own help (`dforge` with no args). Regenerate after adding commands.*

DiscForge exposes **272 commands**. Many read-only commands accept `--json` for machine-readable output.

All analysis is clean-room: DiscForge identifies, verifies, and preserves — it never circumvents, strips, or defeats a protection measure.


## Capstone & disc identity

- `disc-report <image>     Identify a disc and run every matching parser (one report)`
- `identify <file>       Say what a file is (any format DiscForge knows)`
- `inspect <image.cdi>   Show version, sessions, and track layout`
- `disc-print <scan.json> [b.json]   Physical-copy fingerprint from an error scan; compare two`
- `disc-genealogy <collection.json>  Provenance family tree + authenticity/counterfeit verdicts across a collection`
- `mastering-print <image> [--json] | compare <a> <b> [--json]  Derives a mastering fingerprint from the ISO 9660 volume descriptor — system/volume/publisher/data-preparer/application (mastering-tool) ids and the creation/modification timestamps a mastering house stamps, plus a hash of the descriptor and trailing padding. Two pressings of a title share these; a reproduction re-mastered from the same game files diverges. compare flags that divergence (IDENTICAL / DIVERGENT MASTERING / DIFFERENT VOLUME). Characterises the disc's own metadata only.`
- `disc-bom <iso>          Technical bill-of-materials: engine, middleware, runtime, build date`
- `disc-date <iso>         Date a disc from its ISO timestamps; flag re-mastering / tampering`

## Console & platform readers

- `gcm-info <image>        GameCube disc: boot header + file tree; for a Wii disc, the volume header + partition table (contents not read)`
- `gcm-banner <image> <out.png>  Extract a GameCube disc's banner icon (opening.bnr)`
- `gcm-extract <image> <out-dir>  Extract the GameCube disc's file tree to a folder`
- `tpl-info <file.tpl>     List textures in a GameCube/Wii TPL (size + GX format)`
- `tpl-extract <file.tpl> <out>  Decode TPL textures to PNG (--index N)`
- `wbfs-info <file>        List the Wii/GameCube discs in a WBFS container (slot, game id, title, sizes)`
- `wbfs-extract <file> <slot> <out.iso>  Rebuild one disc's ISO from a WBFS container (contents are copied as-is, not decrypted)`
- `saturn-info <image>     Identify a Sega Saturn disc from its header`
- `pcfx-info <image> [--json]  Identify a NEC PC-FX disc from its "PC-FX:Hu_CD-ROM" boot signature (found anywhere in the data area) and surface the readable boot-header text (title/copyright)`
- `segacd-info <image>     Identify a Sega CD / Mega-CD disc from its header`
- `neogeo-ipl <ipl|image>  Parse a Neo Geo CD IPL.TXT boot script (load list) (.gdi, .cue MIL-CD, .cdi, or a raw .bin/.iso)`
- `boot-catalog <iso>      Decode a bootable disc's El Torito boot catalog`
- `opera-ls <image>        List the 3DO Opera file system of a 3DO disc image`
- `ipbin-info <image> [--json]  Identify a Dreamcast disc from its IP.BIN boot header, incl. disc number and a boot-CRC/signature integrity check`
- `pvr-info <file.pvr> [--json]  Describe a Dreamcast PVR texture header (colour/data format, dimensions, GBIX global index) with a structural integrity check`
- `pvm-info <file.pvm> [--json]  List the PVR textures in a Dreamcast PVM archive (recorded filename, format, dimensions) and check the count`
- `mpeg-info <file> [--json]  Describe an MPEG program stream (VCD/SVCD .mpg, DVD .VOB, Dreamcast Sofdec .sfd): video dimensions, aspect, frame rate, MPEG-1/2, elementary streams, CRI ADX detection`
- `cdi-console-info <image>  Identify a Philips CD-i (Green Book) disc and list its filesystem (pure CD-i or CD-i Bridge)`
- `cdi-extract <image> <path> <out-file> | <image> <out-dir> --all  Extract a file (or every file) from a Philips CD-i disc image, handling the Mode 2 Form 1 / Form 2 sector mix so real-time streams (e.g. /MPEGAV/*.DAT) come out whole. Read-only.`
- `psp-info <image>        Read a PSP UMD's PARAM.SFO metadata and filesystem (.iso, .cso or .zso; --sfo dumps every SFO key/value)`
- `pbp-info <EBOOT.PBP>    List a PSP PBP package: version + each sub-file's size, and its PARAM.SFO title/id/category if present`
- `pbp-extract <EBOOT.PBP> <dir>  Write each non-empty PBP sub-file to <dir>/<name> (DATA.PSP is extracted raw and is NOT decrypted)`
- `bdmv-info <file|folder> Show a Blu-ray playlist (.mpls), clip-info (.clpi), or enumerate titles from a BDMV folder`
- `ps2-info <image>        Identify a PlayStation 1/2 disc (game ID, region) from SYSTEM.CNF`
- `rvz-info <image>        Identify an RVZ/WIA container and show its metadata`
- `nkit-info <image> [--json]  Detect an NKit-scrubbed GameCube/Wii image and read its recovery block (source CRC32 for Redump matching, game id, update-partition backup)`
- `gc-verify <image> [--json]  Single-image GameCube health check: sane boot header, DOL/FST offsets, bi2-vs-game-code region agreement, and size class (flags scrubbed/trimmed/truncated dumps)`
- `gc-junk-map <image> [--json]  Map a GameCube disc's non-game padding (gaps between boot/apploader/DOL/FST/files + tail) and classify each region — junk present / zeroed (scrubbed) / structured (tamper flag)`
- `dvd-layerbreak <pfi.bin|.physical> [--image f] [--sectors N] [--json]  Parse a DVD PFI: book type, layers, PTP/OTP, and the dual-layer layer-break LBA, verified against the data area / image size`
- `layerbreak-pick <total-sectors> [--target N] [--cells a,b,..] [--max-layer N] [--seamless]  Choose a legal DVD-DL layer break (nearest cell/ECC boundary, both layers within capacity)`
- `capacity-check <image-sectors> <cd74|cd80|dvd5|dvd9|bd25|bd50|N> [--overburn]  Check an image against media capacity: fits / underburn / overburn / too-large`
- `rom-info <file>         Identify a cartridge ROM (N64, SNES, Genesis, GB/GBC, GBA, NES, and more) and print its No-Intro CRC32/MD5/SHA1`
- `rom-integrity <file> [--json]  Recompute a cartridge's own internal checksums from its data (Game Boy header + global, Sega Genesis content, GBA header + boot logo) to catch a bad dump whose header still looks plausible`
- `n64-info <rom>          N64 CIC boot-chip ID + CRC1/CRC2 boot-checksum verify`
- `fds-info <file.fds> [--json]  Read a Famicom Disk System image (raw or fwNES-wrapped): each side's identity (game code, maker, side/disk number) and its file table — name, type (PRG/CHR/VRAM), load address, size`
- `gdi-info <disc.gdi>     Show a Dreamcast GD-ROM track layout and validate it against the track files beside it`
- `gdi-browse <disc.gdi>   List the game filesystem on the high-density track --extract <dir> writes the files out`
- `milcd-to-cdi <in.cue> <out.cdi>  Convert a Dreamcast MIL-CD Redump bin/cue to a two-session CDI. --version v2|v3|v35, --gap <sectors>`
- `xiso-ls <image.iso>     List files in an Xbox XDVDFS image (--extract <dir>)`
- `create-xiso <folder> <out.iso>   Build an Xbox XISO from a folder`
- `god-info <header>       Identify an Xbox 360 GOD package (type, size, Data#### inventory)`
- `iso-create <folder> <out.iso> [--volume-id N] [--no-joliet] [--rock-ridge] [--boot <img> [--boot-emulation no|floppy144|floppy288|floppy12|harddisk]]   Build a standard ISO 9660 data-disc image from a folder tree: Joliet (long/Unicode names) on by default, --no-joliet for plain 8.3, --rock-ridge for POSIX names. Pass --boot with your own boot loader to make an El Torito bootable disc (no-emulation default, right for isolinux/GRUB). Streams out with constant memory, so any size is fine.`
- `floppy-info <image>     List a floppy image's contents (auto-detect C64 D64, Amiga ADF, or DOS FAT12/16/32 .img), with VFAT long file names`
- `floppy-image <drive> <out.img>  Image a floppy disk to a flat .img (raw 512-byte sectors). Windows: <drive> is the drive letter (needs an elevated shell); macOS/Linux: pass a device path such as /dev/fd0. Reports the geometry (1.44 MB, 720 KB, …); pair with floppy-info / fat-ls / fat-lint. Preservation only.`
- `floppy-extract <image> <path-in-image> <out>  Extract one file from a floppy image`
- `woz-info <file.woz>     Inspect an Apple II WOZ image (Applesauce): disk type, tracks, protection flags (sync/weak bits), metadata, CRC-32`
- `scp-info <file.scp>     Inspect a SuperCard Pro flux image: tracks, revolutions, RPM, flux tick resolution, checksum`
- `kryoflux-info <file.raw>  Inspect a KryoFlux raw stream: flux-transition count, index pulses, RPM, sample clock, hardware/firmware info`
- `d88-info <file.d88>     Inspect a PC-98 / PC-88 D88 floppy image: disk name, media type (2D/2DD/2HD…), track & sector geometry, multi-disk detection`
- `dvd-ifo <dump|build> …  Dump a DVD's structure to editable JSON, or rebuild IFOs from it`
- `dvd-nav <VTS_xx_0.IFO>  Map DVD program chains; flag unreferenced (hidden) PGCs`
- `scummvm-detect <path>   ScummVM Advanced-Detector fingerprints (size + MD5 of the first 5000 bytes) for a game folder or file. --recursive, --bytes N to change the hashed length`
- `scummvm-export <cue> <dir>  Export a disc into a ScummVM game folder: data files + each CD audio track as trackNN.wav. --flac/--ogg re-encode the audio in-process (no ffmpeg needed); --high = better OGG`
- `gameaudio-info <file>   Read a game-music file's metadata/structure (auto-detect PSF/PSF2, SPC, VGM, NSF): system, tags, duration. No playback`

## Filesystems & partitions

- `ls <image.cdi>          List files inside the image (ISO 9660 or UDF, auto-detected) --iso 8.3 names, --joliet, --udf force a filesystem`
- `browse <image>          List files inside a .cdi or .iso (ISO 9660 or UDF) --extract <dir> writes them out, --only <text> filters`
- `extract-files <image.cdi> <dir>  Extract the filesystem contents`
- `disc-fs <image.iso>     Identify every filesystem a disc carries (ISO/Joliet/UDF/HFS/CD-XA)`
- `cue-check <sheet.cue>   Check a cuesheet against the data file it describes: indexes inside the file, track types consistent, arithmetic reaching the end. Exit 2 on errors, 1 on warnings`
- `cue-repair <in.cue> [out.cue] [--json]  Fixes the everyday ways a cue breaks (where cue-check only reports them): a FILE line pointing at the wrong name — corrected against the actual track file beside it, including a lone orphan-file reconciliation — tracks numbered out of order, and a missing INDEX 01; then re-emits a clean, normalised cue, reporting every change and anything it could not safely fix. Dry-run without <out.cue>. Rewrites the cue text only.`
- `hfs-ls <image>          Walk a classic Mac HFS volume: files, folders, fork sizes`
- `hfs-lint <image> [--json]  Read-only structural integrity check for a classic Apple HFS volume (the Mac side of a hybrid disc): validates the Master Directory Block signature and geometry, that the catalog B-tree walks cleanly, that the recorded file/directory counts match the tree actually present, and that every file's data- and resource-fork extents lie inside the volume. Completes the filesystem-lint set alongside iso-lint / udf-lint / fat-lint; validated against genisoimage and hformat HFS images.`
- `hfs-resources <image> [macpath]  List the resources in each file's Mac resource fork (icons, vers, code, ...)`
- `hfs-orphans <image>     Carve HFS free space: leftover/deleted data the catalog hides`
- `udf-orphans <image>     Carve UDF free space: leftover/deleted data via the space bitmap`
- `fat-ls <image>          List a FAT16/FAT32 volume (boot images, hybrid FAT partitions, long names)`
- `fat-lint <image> [--json]  Read-only structural integrity check for a FAT12/16/32 volume (floppy image, El Torito boot image, hybrid FAT partition, card dump): validates the BPB geometry and boot signature, that the redundant FAT copies agree, that every cluster chain is well-formed (no free/bad/out-of-range link, no loop), that no two chains cross-link a cluster, and that no allocated cluster is orphaned (lost). The fsck-style pass a dump needs before it's trusted; cross-validated against dosfsck.`
- `fat-extract <image> <out-dir>  Extract a FAT16/FAT32 volume's tree (--only /PATH)`
- `fs-orphans <image>      Auto-detect HFS/UDF and carve free space (both on a hybrid)`
- `disk-info <image>       Read a whole-disk image's partition table (auto-detect MBR, GPT, or PS2 APA) and the filesystem in each partition`
- `rdb-info <image>        Read an Amiga Rigid Disk Block: geometry + partitions + FS`
- `apm-info <image>        Read an Apple Partition Map (Mac / hybrid-CD partitions)`
- `create-udf <folder> <out.udf> [--udf-version 1.02|1.50]  Build a UDF filesystem image from a folder --volume NAME sets the volume label`
- `create-udf-bridge <folder> <out.iso>  Build a UDF-bridge image readable as BOTH ISO 9660 (with Joliet) and UDF 1.02, sharing one copy of the file data --volume NAME sets the label; --json for JSON`
- `dvd-video-plan <VIDEO_TS-folder>  Validate a VIDEO_TS folder and show the DVD-Video on-disc file order`
- `dvd-video-build <VIDEO_TS-folder> <out.iso>  Assemble a VIDEO_TS folder into a DVD-Video ISO+UDF image (files in on-disc order)`
- `dvd-video-fix <VIDEO_TS-folder> [--apply]  Rewrite IFO sector pointers + refresh .BUP to match file sizes (Fix VTS Sectors); dry-run without --apply`
- `bdmv-plan <BDMV-folder>  Validate a Blu-ray BDMV folder (index/MovieObject/PLAYLIST/CLIPINF/STREAM)`
- `bdmv-build <BDMV-folder> <out.iso>  Assemble a BDMV folder into a BD-Video UDF 2.50 image`
- `iso-rebase <in> <out> <baseLBA>  Shift an ISO's LBAs (GD-ROM fix; base 45000)`

## Convert & create

- `convert <in> <out>    CDI <-> BIN+CUE / ISO / GDI / NRG, or MDS -> CDI (.cdi<->.cue, .cdi<->.iso, .mds->.cdi)`
- `disc-convert <in> <out>  Universal hub: any format -> any via a canonical model (.cue .chd .iso .cso .zso .wbfs .cdi .nrg .mds .gdi .ccd in; .cue .chd .iso .cdi .nrg out)`
- `create <dir> <out.cdi>  Build a data CDI from a folder (--volume NAME, --rock-ridge)`
- `create-audio <out.cdi> <a.wav> [b.wav ...]  Build an audio CD from WAVs --gapless, --74, --postgap [sectors]`
- `build-raw <src> <out>   Compose a RAW DAO image (lead-in + subcode) from a .cue or .cdi. --subcode pq|cooked|raw (default cooked)`
- `cu2 <write|verify> <cue> [out.cu2]  Read/write/verify the Cybdyn CU2 track-map sidecar (PSIO/xStation): emit a dialect-free absolute-LBA track map from a cue, or cross-check an existing CU2 against the cue's geometry`
- `ode-export psio <cue> <out-dir> [--name N]  Lay a preserved PS1 dump out for a PSIO/xStation optical-drive emulator: a game folder holding the track bin(s), the cue, and a generated CU2 track map, ready for the SD card`
- `ode-layout <gdemu|rhea|phoebe|mode> <games-dir> <out-dir> [--json]  Arrange a set of already-converted games (one sub-folder per game) into an ODE's SD-card layout: GDEMU and Rhea/Phoebe use sequentially numbered folders with folder 01 reserved for the menu (games from 02) plus the per-game name/disc sidecars the menu managers read (and a root Rhea.ini/Phoebe.ini); MODE uses free-form named folders it scans itself. It lays out folders + images + sidecars only — the boot menu/index is built by the device's own tool (GDMENU Card Manager / RMENU) by design. PSIO: use ode-export psio.`
- `extract <cdi> <dir>   Extract tracks to ISO/WAV (--raw for full sectors)`
- `vcd-control <out-dir> [--album N] [--svcd] [--entry T:M:S:F ...]  Write INFO.VCD/ENTRIES.VCD`
- `m3u <out.m3u> <disc1> <disc2> [...]  Build a multi-disc M3U playlist (order preserved)`
- `drives                  List optical recorders and their read/write capabilities (CD/DVD/BD, TAO/DAO/RAW-DAO, loaded media). Windows via the OS device stack; macOS via system_profiler. Linux has no optical backend.`
- `burn <image.iso> [drive-letter] [--verify] [--speed N]  Burn a data ISO to a blank CD/DVD/Blu-ray, optionally verifying the written disc. On Windows it writes via the IMAPI2 stack (give the target drive letter); on macOS it writes to the inserted disc via hdiutil (drive letter ignored). For loose files, author an image first (iso-create / create-udf / create-udf-bridge / create-xiso) and burn that. Use `drives` to list recorders.`
- `read-disc <drive> <out.iso> [--continue-on-error] [--retries N]  Image a data disc (DVD, Blu-ray, or data CD/DVD) to a flat .iso by copying every 2048-byte sector via READ(10). Pair with `burn` to clone a personal, unencrypted disc. Clean-room: refuses any disc that declares a copy-protection system (CSS/CPRM/AACS) and stops on a copy-protected sector. Windows uses the SPTI stack; on macOS/Linux the OS exposes a data disc as a block device, so it prints the exact `dd` command instead. For an audio or mixed-mode CD, rip it track-by-track in the GUI (Read Disc). --continue-on-error zero-fills unreadable sectors and lists them; --retries N sets per-sector re-reads (default 3).`
- `raw-dump <drive> [--stream-read]  Drive/media diagnostic for the Hitachi-LG GDR-816x DVD-ROM family (GDR-8161B/2B/3B/4B): identify the drive and report its capabilities, and with --stream-read confirm a standard READ(12)+streaming read where a plain read is refused. Read-only; reports bytes as-is. It does NOT descramble or decode console (GameCube/Wii/GD-ROM) disc formats and does not produce console game images.`

## Verify, preserve & catalog

- `verify <cdi> [--checksums]  Structural checks + per-track CRC-32`
- `compare <a.cdi> <b.cdi>  Diff two images (structure + per-track CRC-32)`
- `checksum <file>         CRC-32 + MD5 + SHA-1 + SHA-256 in one pass --write [sha256|md5|sha1|sfv|all]   write sidecar(s) --verify   check against an existing sidecar`
- `preserve pack <manifest.json> <file> [...] [--protection raw.bin]  Hash-manifest a set (+ self-digest)`
- `preserve verify <manifest.json>  Prove a set is byte-for-byte what was recorded`
- `preserve-master build <image|.cue> [--out f.dpm.json] | verify <f.dpm.json>  The DiscForge Preservation Master (DPM): one open sidecar fusing identity, per-file fixity (CRC32/MD5/SHA-1/SHA-256 + FastCDC Merkle root), the completeness certificate and the clean-room protection profile; verify proves every member byte-for-byte`
- `flux pack <raw> <out.dfflux> [--rate --bits --channels --rpm --profile --note] | info <f.dfflux> [--json]  Store a raw optical RF/flux capture in the open DFFLX1 container with its calibration metadata + payload CRC (phase-1 low-level preservation; the EFM/CIRC demodulator is a later stage)`
- `lineage <keygen|init|append|sign|verify|show> …  Append-only, signed chain-of-custody for a dump`
- `library scan <dir> [--dat f] [--html out.html]   Identify+hash a whole tree, verify vs a DAT; --html writes a friendly color-coded audit dashboard (KPI cards, per-file status, staged rename preview, what's missing) — the approachable ClrMamePro alternative`
- `catalog-export <dir> [--dat f] [--json out.json] [--csv out.csv]   Write a portable, machine-readable catalog of an optical archive — one index of every disc's identity, size, CRC-32/MD5/SHA-1 and (with a DAT) verification status. The index you keep beside a NAS or cloud copy so anything can find and re-verify a disc without re-reading it; JSON for programs, CSV for a spreadsheet/NAS index. The bridge between local cataloguing and off-site backup. Read-only.`
- `library rename <dir> --dat f [--apply]  Rename verified files to canonical names`
- `library-watch <dir> [--update]   Watch a collection for silent corruption (bit rot)`
- `library-report <dir> <out.html> [--dat f]  Scan a folder to a shareable HTML dashboard`
- `consensus <keygen|attest|verify>  Federated, signed cross-dumper consensus ledger`
- `disc-genome <a.cue> [b.cue]  Offset-invariant disc fingerprint; compare two rips for same-disc`
- `remaster <pack|rebuild|verify> …  Decompose an ISO to a recipe+store and rebuild it byte-exact`
- `submission-info <image> [--out f]  Redump-style hashes/cuesheet/subchannel for a dump`
- `submission-pack <image> <out-dir> [--game N]  Assemble a submission-ready folder (dump+info+dat+cue)`
- `dat-diff <old.dat> <new.dat>  Compare two DAT revisions: added/removed/changed games`
- `dat-build <dir> <out.dat> [--name N] [--recursive]  Hash a folder into a Redump-style DAT`
- `rebuild <src> <dest> --dat f [--per-game] [--move] [--apply] Rebuild a clean, DAT-named set from a messy folder`
- `torrentzip <out.zip> <file> [...]   Write a deterministic TorrentZip-structured archive`
- `hashgen <sfv|md5|sha1> <out> <file> [...]  Write a checksum sidecar for the files`
- `hashverify <sidecar.sfv|.md5|.sha1>  Re-hash the referenced files and report OK/FAIL`
- `frontend-export <retroarch|gamelist> <dir> <out> [--name N] [--dat f] Write a RetroArch .lpl or EmulationStation gamelist.xml for a folder`
- `collection-archive <build|verify|extract>  Dedup a library to unique blobs; rebuild any disc exact`
- `vault <create|check|heal>  Self-healing container: Reed-Solomon parity repairs bit-rot`
- `par2-verify <file.par2> [--json]  Read & verify a PAR2 (Parchive) recovery set: checks each packet's MD5, verifies protected files slice by slice, and reports repairability against the available recovery slices`
- `chunk-manifest <file...> [--json]  FastCDC content-defined chunking + Merkle root; deduplicates a set to unique chunks (shift-tolerant) with a reconstruction proof`
- `fuzz-parsers <seed> [--iterations N]  Robustness-fuzz the binary format parsers with mutated inputs; flags unclean crashes/hangs (vs clean format rejections)`
- `disc-semdiff <a> <b> [--json]  Region-level, shift-tolerant diff of two images: where they diverge (not a byte wall)`
- `completeness-check <sheet.cue> [--json]  Dump-coverage certificate: reconcile cue layout, data-file size and subchannel sector count; flag gaps and note what a bin/cue can't hold`
- `pregap-check <sheet.cue> [--json]  Audit a cue's track pregaps against PlayStation/Redump convention: track 1 at 00:00:00, a 2-second (150-sector) pregap at the data/audio boundary, no negative gaps, sequential track numbers`
- `subq-map <disc.sub> [--json] [--form packed|interleaved|pq16]  Recover each track's true INDEX 00 (pregap) and INDEX 01 (body) and the real pregap length from a captured subchannel sidecar — the way Redump derives a disc's pregaps, straight from the Q channel rather than a guessed convention (auto-detects the 96-byte form by CRC validity). Read-only.`
- `redump-cue <in.cue> <disc.sub> <out.cue> [--snap-pregap]  Re-cut a split bin/cue at the subchannel's INDEX 00 boundaries so the track split matches Redump's (pregap at the head of its own file, INDEX 00/01), letting the set match a Redump checksum. Byte-preserving: the concatenated program area is unchanged, only the cut points move; new files are written and the originals left untouched. --snap-pregap normalises a gap within two sectors of 150 to the exact 2-second convention.`
- `bad-sectors <map.badsectors.json> [--json]  Show a dump's unreadable-sector map — the sectors a drive could not read at capture, which a checksum can never reveal (a zero-filled hole hashes like data). Reports the total, genuine damage vs. harmless track-boundary holes, the coalesced runs, and where each hole lands inside its track file. The map is written at capture, carried through convert into the per-track view, and folded into preserve-master so a holed dump reads as INCOMPLETE.`
- `redump-diff <cue> <dat> [--game "name"] [--json]  Explains WHY a dump does or doesn't match Redump, not just yes/no: reconciles each file with the catalogued entry and names the cause — a wrong track split (total size matches, per-track doesn't → redump-cue), padding/truncation (off by whole sectors), a data-track content difference (region/version) or an audio-track one (read-offset), and, from a sibling .badsectors.json, the exact holes that block a match. Analysis only.`
- `dump-audit <cue|image> [--dat f] [--json]  The plain answer to "is my dump good?": fuses structural completeness, the unreadable-sector map, an EDC/ECC audit of the data sectors, the end-of-disc sectors (where a truncated read hides), pregap conformance, and — with --dat — the Redump match into ONE verdict (GOOD / SUSPECT / BAD), each flag naming its specific tell. Catches the silent-failure signatures (a flipped byte, a zero-filled tail, a recorded hole) that make an image look fine but be wrong. Analysis only.`
- `read-stability <pass1> <pass2> [pass3 ...] [--sector-size N] [--json]  Disc-rot early warning without a C1/C2 scanner: a healthy disc reads identically every time, a failing one returns different bytes for the same sector across passes as the drive papers over marginal reflectivity. Compares several full reads and flags the unstable sectors (the leading edge of degradation), grading the disc STABLE / MARGINAL / DEGRADING. Analysis only.`
- `verify-convert <a> <b> [--json]  Proves a format conversion kept the disc byte-for-byte: decodes BOTH images (a bin/cue, a CD .chd, or a raw .bin) to their raw sector bytes and compares them exactly, reporting LOSSLESS or the precise divergence — a size delta from a dropped/added track or subchannel, or the first differing sector. The "did my CHD conversion lose anything?" check nothing else provides. Read-only.`
- `fs-verify <image> [--json]  Cross-checks every filesystem view a disc carries (ISO 9660, Joliet, UDF) and confirms they describe the same files with the same bytes — matching by content hash, not name, so ISO 8.3 mangling never trips it. Reports AGREE, DIVERGENT (a file reachable from one filesystem but not the other — tampering or content hidden from one view), or INCOMPLETE (a filesystem declared but unreadable — a truncated dump). On a Mac+PC hybrid it also reads the HFS (Mac) side and catalogues shared / Mac-only / PC-only files (an expected hybrid difference, never counted as a fault). The bridge/hybrid-disc integrity check nothing else provides. Read-only; accepts .iso/.cdi/.bin/.cue/.img.`
- `chd-verify <image.chd> [--parent p.chd ...] [--json]  Checks a CHD's integrity without extracting it: decompresses every hunk, checks each against its map's CRC-16, and confirms the whole decompressed image matches the SHA-1 the CHD stores of itself — the same proof chdman's verify performs. Reports VALID, CORRUPT (a damaged hunk or SHA-1 mismatch), UNVERIFIED (an uncompressed CHD with no stored hash), or UNSUPPORTED. The archival-integrity check for the emulation world's dominant format; pass --parent for a delta CHD. Read-only; writes nothing.`
- `disc-diff <a> <b> [--json]  Compares two disc images at the FILE level — reading each through its filesystem and comparing by content hash — and reports what changed: files added, removed, changed in content, or moved/renamed (same bytes, new path). Answers "what actually differs between these two discs?" for two pressings, a patched vs original disc, or two revisions. Filesystem- and container-agnostic (an ISO and a UDF bridge of the same files read as IDENTICAL). Distinct from verify-convert (raw byte-for-byte) and redump-diff (DAT match). Read-only.`
- `redump-prep <in.cue> <out-dir> [--sub f] [--snap-pregap] [--dat f --game "n"] [--offset N] [--json]  One-step submission prep: re-cuts the tracks to the subchannel's Redump boundaries, carries the unreadable-sector map forward, checks pregap conformance and completeness, writes the redump.org submission text, and (with --dat) diffs the result — returning a single SUBMISSION-READY / NOT-READY checklist. A read offset is recorded for the submission, never applied (the payload is left byte-for-byte as dumped).`
- `dump-provenance <path...>  Infer what tool produced a dump from its fileset + geometry`

## Conformance & audit

- `iso-lint <iso>          Strict ISO 9660 conformance check (spec violations)`
- `udf-lint <iso|udf> [--json]  Strict UDF conformance check: walks the volume like a driver (Volume Recognition Sequence, Anchor@256, Main VDS, Partition/Logical-Volume descriptors, Integrity Descriptor, File Set Descriptor, root File Entry) and validates every descriptor tag's checksum and CRC and that its tag location is recorded correctly — partition-relative inside the partition. The exact check that catches a "File Set Descriptor not found". Read-only.`
- `iso-pathtable <iso>     Audit the ISO 9660 path table (L/M agreement, parent tree)`
- `iso-rockridge <iso>     Recover Rock Ridge POSIX metadata (perms, owners, symlinks, times)`
- `redbook-audit <cue>     Strict Red Book (CD structure) conformance check`
- `premaster-check <cue>   Master-readiness gate: structure + capacity + data integrity`

## Forensics & protection

- `protection-scan <iso>   Fingerprint copy protection (SafeDisc/SecuROM/LibCrypt…) as metadata --raw <raw.bin>  also fuse on-disc signals (twin sectors, error band)`
- `protection-profile <image> [--json]  Unified clean-room protection profile: schemes fingerprinted + where their signatures sit + a capture-completeness assessment (does this image's mode actually preserve each — filesystem/raw-body/subchannel/timing — else the recapture that would)`
- `twin-scan <raw.bin>     Detect twin / re-addressed sectors (header-address protection)`
- `error-pattern <in.bin>  Classify failing sectors: scratch/rot (recover) vs protection (preserve)`
- `weak-sectors <raw.bin>  Predict channel-weak sectors (scramble+EFM+DSV physical model)`
- `efm-spectrum <raw.bin>  EFM run-length spectrum, duty asymmetry, DSV, grade`
- `scratch-verdict <in.bin>  Per-scratch recovery outlook (corrected/concealed/re-read)`
- `recovery-map <in.bin> <out.svg>  SVG map coloured by recovery outlook per region`
- `disc-anomalies <iso>    Find hidden / orphan data no file or ISO structure explains`
- `covert-scan <iso>       Hunt for hidden data in zero-expected regions (slack, system area)`
- `ring-code "<runout>" | group <json>  Parse IFPI ring codes; group discs by plant/master`
- `dpm <scan.csv>          Data-position timing: detect ring protection, fingerprint layout`
- `bler <scan.csv>         C1/C2 surface-quality report: BLER, E22/E32, Red Book pass/fail`
- `scan-import <scan.txt> [--family cd|dvd|bd] [--id N] [--emit print|rot|bler|json]  Import an Opti Drive Control / Nero DiscSpeed / KProbe / DVDInfoPro quality scan (CD C1/C2, DVD PIE/PIF/POF, BD LDC/BIS) into DiscForge's model; --emit re-serialises it for disc-rot, disc-print or bler`
- `disc-rot <history.json> Triage C1/C2 scan history over time; predict which discs to dump first`
- `rot-kinetics <history.json> [--temp C --rh %] [--json]  First-order (Arrhenius/Eyring) decay fit + survival forecast with confidence band`
- `libcrypt <file.sub>     Characterise LibCrypt: variant, magic/key material, per-sector CRC deltas. --sbi <out> writes the sidecar`
- `subch <file.sub>        Analyse a captured raw sub-channel sidecar: Q-CRC validity, LibCrypt-style protection fingerprint`
- `health-map <in.bin> <out.svg>  Render a per-sector EDC/ECC health heatmap (SVG)`
- `disc-cluster <path...>  Group un-identified dumps by content — same title, different variants`
- `hidden-sessions <cue>   Map every session; flag data sessions a naive rip would skip (CD Extra)`
- `matter-map <in> <out.svg>  Classify each region (zero/text/structured/high-entropy) as SVG`
- `phylo <dir|iso...>      Build a family tree of a title's releases from file deltas`
- `pregap-scan <audio.bin> Detect hidden-track audio in a pregap/gap (HTOA)`
- `disc-delta <base.iso> <target.iso> <out.delta>  File-level delta carrying only what changed`
- `disc-patch <base.iso> <in.delta> <out.iso>      Rebuild the target byte-exact from base + delta`
- `offset-detect <rip.bin> <reference.bin>  Detect the CD-DA read offset between two PCM rips`

## Recovery & merge

- `recover-oracle <frames> Model CIRC recovery: can a burst of N frames be corrected?`
- `recover-toc <raw.sub>   Rebuild the TOC from the Q sub-channel (dead lead-in recovery)`
- `dump-merge <out> <in1> <in2> [in3 ...]  Merge several imperfect rips of the SAME disc into one image (EDC-verified where possible)`
- `merge-cert <out> <in1> <in2> [...] [--sector-size N] [--key f | --gen-key] [--json] | verify <cert> [out in...]  Bad-sector-aware multi-copy merge that writes a signed, checkable provenance certificate: how EVERY sector was decided (agreed / EDC / voted / single-source / unrecovered) and which copy it came from. Each input's sibling .badsectors.json is honoured — a copy's unreadable sectors are excluded from the vote, not counted as data. verify checks the signature and re-confirms the input/output hashes the certificate binds. Pure recovery + provenance.`
- `c2-merge <out.bin> <in1.bin> [in1.c2] <in2.bin> [in2.c2] ...  Byte-level C2 consensus recovery: merges several raw 2352-byte reads of the same disc using each read's C2 error pointers (a redumper/DIC .c2 file, 294 bytes/sector). For every byte it takes a value a read's C2 marks GOOD, so a sector NO single read got whole is reassembled from each read's good bytes and confirmed by its EDC — the recovery a sector-level merge can't do. Reports how many sectors were rescued from fragments. Pure read-side recovery.`
- `dvd-ecc self-test | repair <block.bin> <out.bin>  DVD sector-layer error correction (RS-PC) on a logical 208×182 ECC block: inner code PI = RS(182,172) per row, outer code PO = RS(208,192) per column, a product code that hands a row the inner code can't fix to the outer code as an erasure (up to 16 whole rows recovered). Reuses DiscForge's GF(2^8) RS engine; validated by round-trip. NOTE: the ECMA-267 physical byte→block interleave is not verified in this build — confirm against a real raw ECC block before real-disc use.`
- `flux-demod self-test | encode <in> <out.dff> [--cell N --jitter J] | decode <in.dff> <out>  Demodulates an optical flux/RF capture into the EFM channel bitstream — the stage FluxContainer deferred: recovers the channel-cell clock from transition timing and quantises each pit/land interval to EFM's 3T-11T run-length law (NRZI). Software-first and validated by round-trip against DiscForge's EFM encoder; decoding a REAL disc additionally needs the authoritative ECMA-130 8-to-14 table in the EFM codebook (a data swap). The demodulation stage itself is complete.`
- `collection-triage <folder> [--dat f] [--html out.html] [--json]  A librarian's view of a whole collection, where every other tool works one disc at a time: walks a folder and folds together each dump's Redump match, unreadable-sector map, mismatch diagnosis and content de-duplication into one ranked worklist — verified / INCOMPLETE (re-read) / re-cut (wrong split) / duplicate / check — with a shareable self-contained HTML dashboard. Read-only.`
- `salvage-plan <folder> [--json]  Finds where several unreadable dumps can rescue each other: groups a collection's dumps by title (disc geometry + boot-area anchor), intersects their .badsectors.json hole maps, and reports whether merging the copies would fill every hole (FULLY SALVAGEABLE), some (PARTIAL), or none — with the exact merge-cert command. The discovery layer over merge-cert; nothing else surfaces cross-dump salvage opportunities. Read-only.`
- `reconstruct <out> <in1> [in2 ...]  Best-possible image via agree/EDC/ECC/vote with per-sector provenance (--no-ecc, --provenance <map>)`
- `dump-score <raw.bin>    Score a raw image's dump confidence (0-100 + grade) from EDC`
- `ecc-repair <image.bin>  Rebuild damaged Mode 1 sectors from the Reed-Solomon parity they already carry. --dry-run to check only`
- `fix-modes <image.cdi>   Correct track modes recorded wrongly in a CDI descriptor`

## Raw sectors & offsets

- `view-sector <img> <addr> [--count N] [--descramble]  Annotated hex view of sectors. addr: LBA, mm:ss:ff, or +fileindex`
- `extract-sectors <img> <out> --start <addr> --count N  Pull a sector range --as stored|user|raw2352, --byteswap for audio`
- `inspect-raw <img>       Analyse a raw image: TOC, Q health, CD-TEXT, MCN/ISRC, scrambling, EDC/ECC. --deep checks every sector. Also reads bare 2352 BINs (ECC gold-check on real rips)`
- `raw-verify-readback <golden.img> <readback.bin>  Prove a RAW burn is byte-faithful: compare a disc read-back to the golden image across the main channel, EDC/ECC and every Q frame (the sub-channel check ImgBurn's MD5 verify can't do). --report out.html writes a shareable certificate; --json for scripting`
- `dvd-verify-readback <source.iso> <readback.bin> [--layer-break LBA]  Verify a burned DVD/BD against its source at ECC-block (16-sector) granularity, layer-break aware (attributes mismatches to L0/L1) — tells you WHERE a burn differs, not just an MD5`
- `booktype-trace <trace-file> [--vendor V] [--model M] [--target BT] [--save recipe.json]  Decode a captured SCSI/MMC bitsetting (book-type) command trace and learn a verbatim replay recipe from your own drive — clean-room, never fabricates vendor bytes`
- `read-offset <samples> [in.wav out.wav]  Redump read-offset math; with a WAV, slide it by <samples> (combined drive+disc offset)`

## Multimedia & streams

- `xa-map <raw.bin>        Map CD-ROM XA streams: file/channel, video/audio, interleave`
- `vcd-psd <PSD.VCD> [LOT.VCD]  Decode VCD PlayBack Control: menus, play lists, links`
- `cdg-frames <src> <dir>  Export a CD+G frame sequence as numbered PNGs (--fps N)`
- `cdg-preview <src>       Decode CD+G graphics from a raw image (2448) or a .cue with .sub sidecar. --seconds N (default 30), --out shot.ppm writes a screenshot`
- `cdg-render <file.cdg>   Render a CD+G frame to PNG. --at MM:SS (default end), --out <file.png> (default <name>.png)`
- `cdg-extract <sub> <out.cdg>  Extract the CD+G packet stream from a raw 96-byte/sector sub-channel sidecar`
- `str-demux <in.str> <out-dir>  Split a PSX .str into MDEC bitstreams + audio note --sector-size 2352|2048 (default 2352)`
- `str-frames <in.str> <out-dir>  Decode PSX .str v2 video frames to PNG images`
- `mdec-info <in.str>      MDEC codec params per frame (version, qscale, macroblocks)`
- `vob-demux <in.vob|.mpg> <out-dir>  Split an unencrypted MPEG program stream (VOB/MPG) into elementary video/audio/subpicture streams (no CSS decrypt)`
- `adx-decode <in.adx> <out.wav>  Decode a CRI ADX ADPCM stream to a 16-bit WAV`
- `dsp-decode <in.dsp> <out.wav>  Decode a Nintendo GameCube/Wii DSP-ADPCM stream to a 16-bit mono WAV (reconstructs from the file's own eight predictor-coefficient pairs)`
- `vab-info <file.vab>     Read a PlayStation VAB (VAG bank): programs, tones, VAGs`
- `seq-info <file.seq>     Read a PlayStation SEQ sequence: ppqn, tempo, event count`

## Audio

- `cdtext <file.cdt>       Decode CD-TEXT packs into album/track title & performer`
- `deemph <in.wav> <out.wav>  Apply CD de-emphasis (50/15µs) to a pre-emphasised track`
- `silence-split <in.wav>  Find track boundaries by silence; emit a cue sheet`
- `audio-dynamics <in.wav> DR value, peak/RMS/crest, and clipping detection`
- `hdcd-scan <in.wav>      Detect HDCD encoding hidden in 16-bit PCM least-significant bits`

## Patches, saves & cheats

- `ppf-apply <patch.ppf> <image.bin>  Apply a PlayStation Patch File to an image --undo revert (PPF 3.0), --force skip validation, --dry-run`
- `ppf-create <orig> <mod> <out.ppf>  Build a PPF 3.0 from a before/after image pair --desc, --fileid, --no-undo, --no-validation`
- `ppf-info <patch.ppf>    Show a patch's version, description, size and flags`
- `ppf-convert <in> <out> --to 1|2|3  Rewrite a patch in another PPF revision`
- `ppf-edit <in> <out>     Change a patch's --desc and/or --fileid in place`
- `ips-apply <patch.ips> <image> [--out f]  Apply an IPS patch (in place unless --out)`
- `ips-create <orig> <mod> <out.ips>  Build an IPS patch from a before/after pair`
- `bps-apply <patch.bps> <source> [--out f]  Apply a BPS patch (CRC-verified)`
- `bps-create <source> <target> <out.bps>  Build a BPS patch from a before/after pair`
- `save-convert <in> <out> <op> [--fill FF]  Fix a cartridge save's byte order or size. op: swap16|swap32, pad <size|sram|flash|eeprom4k|eeprom16k|mempak>, trim`
- `rom-convert <in> <out> <op>  Fix a cartridge dump so it matches a DAT. op: z64|v64|n64 (N64 byte order), snes-strip|snes-add, smd|unsmd (Genesis interleave), nes-strip (iNES header)`
- `gci-info <file>         List GameCube saves in a .gci or a memory-card image`
- `gci-extract <card> <index> <out.gci>  Write one save from a card image to a .gci`
- `ps2mc-ecc <card.ps2> [--repair <out.ps2>] [--json]  Verify (and optionally repair) a PlayStation 2 memory card's per-page Hamming ECC. Each 528-byte "with-ECC" page carries a 3-byte code for every 128-byte chunk, which detects any error and corrects any single-bit flip — guarding a preserved save against silent bit-rot. Reports CLEAN / CORRECTABLE / CORRUPT; --repair writes a corrected copy and never touches the input. The save-data analogue of ecc-repair for discs. Read-only otherwise.`
- `n64save-info <file>     Identify an N64 save by size, and list Controller Pak notes`
- `saturnsave-info <file>  List the directory of a Sega Saturn backup-memory image`
- `cheat-decode <platform> <code>  Decode a Game Genie / GameShark code to address/value platform: nes|snes|genesis|gb|gs-ps1`
- `cheat-encode <platform> <address> <value> [compare]  Encode a Game Genie code platform: nes|snes|genesis|gb (hex address/value)`
- `cheat-apply-nes <rom> <code> <out>  Apply an NES Game Genie code to a ROM (NROM)`

## Split / join / licence

- `split <file> <size>     Split into .001/.002/… + .sfv manifest sizes: bytes, 700m, 4g, or fat32 (= 4 GiB - 1)`
- `join <part|base> [out]  Rejoin parts, verifying CRCs + SHA-256 via the manifest`
- `license <keygen|issue|verify|machine-id> …  Manage DiscForge licence keys (see --help) One-game-one-ROM: pick the best region per game from a DAT`

## More commands

- `chd-info <image.chd>    Show a CHD's version, codecs, hunk geometry and CD track layout`
- `chd-create <in.cue|in.img> <out.chd>   Create a CHD (v5) from a bin/cue or raw image`
- `chd-extract <image.chd> <out.bin> [out.cue] [--parent p.chd ...]   Decompress a CD CHD to bin/cue`
- `chd-extract-hd <image.chd> <out.img> [--parent ...]   Decompress a hard-disk CHD to a raw image`
- `ciso-info <image.cso|.zso>   Show a compressed-ISO (CSO/ZSO) header`
- `ciso-to-iso <in.cso|.zso> <out.iso>   Decompress a CSO/ZSO to a plain ISO`
- `iso-to-ciso <in.iso> <out.cso>   Compress an ISO to CSO (zlib)`
- `psx-build <folder> <out.bin> [volume-id] [out.cue]   Build a PlayStation data track from a folder`
- `psx-exe-info <file.exe>   Read a PS-EXE header (load address, entry point, size)`
- `psx-pad <in> <out> [--multiple N | --psexe] [--fill 0xNN]   Pad a PS1 binary to a size/boundary`
- `psx-video-mode <in> --to ntsc|pal [--ppf out.ppf | --out out.bin]   Convert a PS1 game's video mode`
- `vag-extract <file.vag> <out.wav>   Decode a PlayStation VAG (SPU-ADPCM) sample to WAV`
- `xa-extract <raw image> <out.wav> [--sector-size N] [--channel N]   Extract CD-XA ADPCM audio to WAV`
- `tim-info <file.tim>     Describe a PlayStation TIM texture`
- `tim-extract <file.tim> <out.png> [--palette N]   Decode a TIM texture to PNG`
- `tmd-info <file.tmd>     Describe a PlayStation TMD 3D model`
- `tmd2dxf <file.tmd> <out.dxf>   Convert a PlayStation TMD model to DXF`
- `ps1mc-info <card.mcr>   List PlayStation 1 memory-card saves (title, blocks). Alias: psxmc-info`
- `ps1mc-extract <card.mcr> <out-dir>   Extract PS1 saves to individual files. Alias: psxmc-extract`
- `ps1mc-format <out.mcr> [raw|gme|vgs]   Write a freshly-formatted, empty PS1 memory card. Alias: psxmc-format`
- `ps1mc-convert <in> <out> [raw|gme|vgs]   Convert a PS1 memory card between container formats. Alias: ps1card-convert`
- `ps2mc-info <card.ps2>   List PlayStation 2 memory-card files/saves`
- `ps2mc-extract <card.ps2> <out-dir>   Extract PS2 memory-card files`
- `vmu-info <vmu.bin>      List Dreamcast VMU saves`
- `vmu-create <out.bin>    Write a blank formatted 128 KB Dreamcast VMU`
- `vmu-add <vmu.bin> <save.vms> [--name N] [--game] [--protect]   Add a save to a VMU`
- `vmu-extract <vmu.bin> <out-dir> [--force]   Extract VMU saves`
- `vms2vmi <save.vms> <out.vmi> [--desc T] [--name N]   Wrap a raw VMS save as a VMI+VMS pair`
- `dc-scramble <in> <out>  Apply the Dreamcast bootstrap (1ST_READ.BIN) scramble`
- `dc-descramble <in> <out>   Reverse the Dreamcast bootstrap scramble`
- `tod-info <file.tod>     Describe a Dreamcast TOD model file`
- `dvd-info <VIDEO_TS|disc root>   Summarise a DVD-Video's structure`
- `dvd-rewrite <VIDEO_TS|disc root> <out folder> [--keep 1,3]   Rebuild VIDEO_TS keeping selected titles`
- `vcd-info <INFO.VCD|ENTRIES.VCD>   Read a Video CD control/entry file`
- `accuraterip <image.cue> [--db <dBAR.bin>] [--url]   AccurateRip v1/v2 checksums + disc IDs; verify vs a DB record`
- `scan-protection <image.cdi>   Fingerprint copy protection as metadata (identify only)`
- `sbi-make <disc.sub> [out.sbi] [--start-lba N]   Write an SBI from a captured subchannel (LibCrypt preservation)`
- `sbi-info <file.sbi>     Describe an SBI subchannel-patch file`
- `ecm <in.bin> [out.ecm]  Shrink a raw image to ECM (strip regenerable sync/EDC/ECC; lossless)`
- `unecm <in.ecm> [out.bin]   Rebuild the raw image from an ECM file (EDC-verified)`
- `bincue-merge <in.cue> <out.bin> [out.cue]   Merge a multi-bin cue into one bin+cue`
- `bincue-split <in.cue> [out-dir] [base] [out.cue]   Split a single-bin cue into per-track bins`
- `to-ccd <image.cue> [--out basename]   Convert a cue/bin to CloneCD .ccd/.img/.sub`
- `ccd-info <image.ccd>    Read a CloneCD control file`
- `cdr-info <atip-dump>    Read an ATIP dump (blank CD-R manufacturer/dye type)`
- `mount <image>           Mount a disc image, or show how to mount it where supported`
- `transcode <input> <output> [options]   Transcode audio between DiscForge-supported formats`
- `dat-verify <dat-file> <file ...>   Verify one or more files against a Redump/No-Intro DAT`
- `bin2src <file> [--name ID] [--asm] [--per-line N] [--out f]   Emit a file as C/asm source bytes`
- `search <file> (--hex 4d5a | --ascii TEXT) [--limit N]   Search a file for a hex or ASCII pattern`
