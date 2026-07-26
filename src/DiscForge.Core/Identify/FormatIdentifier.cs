// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;

namespace DiscForge.Core.Identify;

/// <summary>What a file was recognised as.</summary>
public sealed record FormatId
{
    public required string Name { get; init; }
    public required string Category { get; init; }
    public string Detail { get; init; } = "";
    public bool Recognised => Name != "Unknown";

    public static readonly FormatId Unknown = new()
    {
        Name = "Unknown", Category = "unrecognised",
        Detail = "no known DiscForge signature matched",
    };
}

/// <summary>
/// Sniffs a file and reports which of the formats DiscForge understands it is —
/// one entry point over every reader: disc images, compressed images, console
/// memory cards, PlayStation asset files and patches. It reads only the small
/// regions where each format's signature lives (the head, a few fixed sector
/// offsets, and the tail), so it identifies even multi-gigabyte images cheaply.
/// Detection is by documented signature, most-specific first.
/// </summary>
public static class FormatIdentifier
{
    private const int HeadBytes = 0x20000;   // 128 KB covers every fixed-offset signature below

    public static FormatId Identify(Stream file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!file.CanSeek) throw new ArgumentException("Identifying a file needs a seekable stream.", nameof(file));

        long len = file.Length;
        var head = new byte[(int)Math.Min(len, HeadBytes)];
        file.Seek(0, SeekOrigin.Begin);
        file.ReadExactly(head, 0, head.Length);

        byte[] tail = Array.Empty<byte>();
        if (len >= 12)
        {
            tail = new byte[12];
            file.Seek(len - 12, SeekOrigin.Begin);
            file.ReadExactly(tail, 0, 12);
        }

        return IdentifyBytes(head, tail, len);
    }

    public static FormatId Identify(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var tail = data.Length >= 12 ? data.AsSpan(data.Length - 12).ToArray() : Array.Empty<byte>();
        return IdentifyBytes(data, tail, data.Length);
    }

    private static FormatId IdentifyBytes(byte[] head, byte[] tail, long len)
    {
        // ---- strong offset-0 magics ----
        if (Ascii(head, 0, "MComprHD")) return Make("CHD", "compressed disc image", "MAME Compressed Hunks of Data");
        if (Ascii(head, 0, "CISO")) return Make("CSO", "compressed disc image", "zlib block-compressed ISO");
        if (Ascii(head, 0, "ZISO")) return Make("ZSO", "compressed disc image", "LZ4 block-compressed ISO");
        if (Ascii(head, 0, "Sony PS2 Memory Card Format ")) return Make("PS2 memory card", "save card", ".ps2");
        if (Ascii(head, 0, "PS-X EXE")) return Make("PS-EXE", "executable", "PlayStation main binary");
        if (Ascii(head, 0, "VAGp")) return Make("VAG", "audio", "PlayStation SPU-ADPCM sample");
        // Game-audio rips (metadata/structure only — no playback). PSF requires a
        // known version byte after "PSF" so it can't swallow arbitrary "PSF…" data.
        if (Ascii(head, 0, "PSF") && head.Length > 3 && IsKnownPsfVersion(head[3]))
            return Make("PSF", "game audio", "Portable Sound Format (PSF family)");
        if (Ascii(head, 0, "SNES-SPC700 Sound File")) return Make("SPC", "game audio", "SNES SPC700 sound dump");
        if (Ascii(head, 0, "Vgm ")) return Make("VGM", "game audio", "Video Game Music log");
        if (MagicAt(head, 0, (byte)'N', (byte)'E', (byte)'S', (byte)'M') && head.Length > 4 && head[4] == 0x1A)
            return Make("NSF", "game audio", "NES Sound Format");
        if (Ascii(head, 0, "MEDIA DESCRIPTOR")) return Make("MDS", "disc image", "Alcohol 120% descriptor");
        if (Ascii(head, 0, "WBFS")) return Make("WBFS", "disc image", "Wii Backup File System");
        if (Ascii(head, 0, "DVDVIDEO-VMG")) return Make("IFO", "DVD-Video", "video manager (VMG)");
        if (Ascii(head, 0, "DVDVIDEO-VTS")) return Make("IFO", "DVD-Video", "video title set (VTS)");
        if (Ascii(head, 0, "MPLS") && BdmvVersion(head, 4)) return Make("MPLS", "Blu-ray", "BDMV movie playlist");
        // "HDMV" is a generic-looking tag, so only claim it when a plausible BDMV
        // version follows — that is what distinguishes a .clpi clip-info file.
        if (Ascii(head, 0, "HDMV") && BdmvVersion(head, 4)) return Make("CLPI", "Blu-ray", "BDMV clip-info");
        if (Ascii(head, 0, "VIDEO_CD") || Ascii(head, 0, "SUPERVCD")) return Make("VCD INFO", "Video CD", "control file");
        if (Ascii(head, 0, "ENTRYVCD") || Ascii(head, 0, "ENTRYSVD")) return Make("VCD ENTRIES", "Video CD", "entry table");
        if (Ascii(head, 0, "PPF30")) return Make("PPF", "patch", "PlayStation Patch File 3.0");
        if (Ascii(head, 0, "PPF20")) return Make("PPF", "patch", "PlayStation Patch File 2.0");
        if (Ascii(head, 0, "PPF10")) return Make("PPF", "patch", "PlayStation Patch File 1.0");
        if (Magic4(head, 0x10, 0x00, 0x00, 0x00)) return Make("TIM", "texture", "PlayStation image");
        if (Magic4(head, 0x41, 0x00, 0x00, 0x00)) return Make("TMD", "3D model", "PlayStation model");
        // RVZ/WIA share a container structure; RVZ adds zstd. Big-endian magic "xxx\x01".
        if (MagicAt(head, 0, (byte)'R', (byte)'V', (byte)'Z', 0x01)) return Make("RVZ", "compressed disc image", "Dolphin RVZ (WIA + zstd)");
        if (MagicAt(head, 0, (byte)'W', (byte)'I', (byte)'A', 0x01)) return Make("WIA", "compressed disc image", "Wii/GameCube Image Archive");

        // ---- cartridge ROM signatures (distinctive magics only, to avoid clashing with discs) ----
        if (MagicAt(head, 0, (byte)'N', (byte)'E', (byte)'S', 0x1A)) return Make("NES", "cartridge ROM", "iNES / NES 2.0");
        if (MagicAt(head, 0, 0x80, 0x37, 0x12, 0x40)) return Make("Nintendo 64", "cartridge ROM", "big-endian (.z64)");
        if (MagicAt(head, 0, 0x37, 0x80, 0x40, 0x12)) return Make("Nintendo 64", "cartridge ROM", "byte-swapped (.v64)");
        if (MagicAt(head, 0, 0x40, 0x12, 0x37, 0x80)) return Make("Nintendo 64", "cartridge ROM", "little-endian (.n64)");
        if (Ascii(head, 0, "LYNX")) return Make("Atari Lynx", "cartridge ROM", ".lnx");
        if (Ascii(head, 0, "COPYRIGHT BY SNK") || Ascii(head, 0, "LICENSED BY SNK")) return Make("Neo Geo Pocket", "cartridge ROM", "SNK licence header");
        if (Ascii(head, 1, "ATARI7800")) return Make("Atari 7800", "cartridge ROM", ".a78");
        // GBA: the fixed 0x96 at 0xB2 plus the start of the Nintendo logo at 0x04.
        if (head.Length > 0xB2 && head[0xB2] == 0x96 && MagicAt(head, 4, 0x24, 0xFF, 0xAE, 0x51)) return Make("Game Boy Advance", "cartridge ROM", ".gba");
        // Genesis / Mega Drive: the "SEGA " console-name tag at 0x100.
        if (Ascii(head, 0x100, "SEGA ")) return Make("Sega Mega Drive / Genesis", "cartridge ROM", ".md / .gen / .bin");

        // ---- floppy-disk images (a distinct medium class) ----
        // These are gated on exact/known sizes so they can't clash with the many
        // other 512-multiple disc images checked further down.
        //
        // Amiga ADF: "DOS" magic in the bootblock plus a standard DD/HD size.
        if ((len == 901120 || len == 1802240) && Ascii(head, 0, "DOS"))
            return Make("ADF", "floppy image", "Amiga (AmigaDOS OFS/FFS)");
        // C64 D64: recognised purely by size, but confirmed by a sane BAM — the
        // directory-track link (track 18 sector 0, bytes 0-1) points at 18/1.
        if (len == 174848 || len == 196608)
        {
            int bam = D64BamOffset();   // track 18 sector 0
            if (bam + 2 <= head.Length && head[bam] == 18 && head[bam + 1] == 1)
                return Make("D64", "floppy image", "Commodore 1541 disk image");
        }
        // DOS FAT12 floppy: 0x55AA boot signature + a sane BPB (512-byte sectors,
        // a real media descriptor). Checked before the generic disc formats and
        // conservative enough not to swallow other 512-multiple images.
        if (len >= 512 && head.Length > 0x1FF &&
            head[0x1FE] == 0x55 && head[0x1FF] == 0xAA &&
            head[0x0B] == 0x00 && head[0x0C] == 0x02 &&   // bytes/sector == 512 (LE)
            head[0x15] >= 0xF0 &&                          // media descriptor
            head[0x0D] != 0 && head[0x10] is >= 1 and <= 2)
            return Make("FAT12", "floppy image", "DOS FAT12 (.img)");

        // ---- text formats ----
        string? text = SniffText(head);
        if (text is not null)
        {
            if (text.Contains("[CloneCD]", StringComparison.OrdinalIgnoreCase)) return Make("CCD", "disc image", "CloneCD control file");
            if (text.Contains("FILE", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("BINARY", StringComparison.OrdinalIgnoreCase)) return Make("CUE", "disc image", "cue sheet");
            if (LooksLikeGdi(text)) return Make("GDI", "disc image", "Dreamcast GD-ROM index");
        }

        // ---- fixed-offset image signatures ----
        // CD-i (Philips Green Book) sits on top of ISO 9660 at sector 16, so it
        // must be checked BEFORE the generic "CD001 → ISO 9660" line below or a
        // CD-i Bridge disc would be mislabelled plain ISO. A pure CD-i disc marks
        // the standard identifier "CD-I "; a Bridge disc keeps "CD001" but carries
        // "CD-RTOS" in the system identifier (32 bytes at 0x8008).
        if (len > 0x8006 && Ascii(head, 0x8001, "CD-I "))
            return Make("CD-i", "disc image", "Philips CD-i (Green Book)");
        if (len > 0x8028 && Ascii(head, 0x8001, "CD001") && ContainsAscii(head, 0x8008, 32, "CD-RTOS"))
            return Make("CD-i", "disc image", "CD-i Bridge");

        if (len > 0x8006 && Ascii(head, 0x8001, "CD001"))
        {
            // ISO 9660; if a UDF Volume Recognition Sequence is also present it's a UDF/hybrid.
            if (len > 0x8806 && (Ascii(head, 0x8801, "BEA01") || Ascii(head, 0x9001, "NSR02") || Ascii(head, 0x9001, "NSR03")))
                return Make("UDF", "disc filesystem", "ISO 9660 + UDF");
            return Make("ISO 9660", "disc filesystem", "CD001 volume");
        }
        if (len > 0x9006 && (Ascii(head, 0x8001, "BEA01") || Ascii(head, 0x9001, "NSR02") || Ascii(head, 0x9001, "NSR03")))
            return Make("UDF", "disc filesystem", "UDF volume");
        if (len >= 0x10000 + 20 && Ascii(head, 0x10000, "MICROSOFT*XBOX*MEDIA"))
            return Make("XISO", "disc filesystem", "Xbox XDVDFS (base 0)");

        // Nintendo optical discs carry a big-endian magic word at a fixed offset.
        // Wii (0x5D1C9EA3 at 0x18) is checked before GameCube (0xC2339F3D at 0x1C).
        if (MagicAt(head, 0x18, 0x5D, 0x1C, 0x9E, 0xA3)) return Make("Wii", "disc image", "Nintendo Wii disc (structure only; contents encrypted)");
        if (MagicAt(head, 0x1C, 0xC2, 0x33, 0x9F, 0x3D)) return Make("GameCube", "disc image", "Nintendo GameCube GCM/ISO");

        // Sega console disc headers sit at the start of the data track's user data:
        // offset 0 for a cooked 2048/ISO track, 16 for a raw Mode 1, 24 for raw Mode 2.
        foreach (int off in new[] { 0, 16, 24 })
        {
            if (Ascii(head, off, "SEGA SEGASATURN"))
                return Make("Saturn", "disc image", "Sega Saturn disc");
            if (Ascii(head, off, "SEGA SEGAKATANA"))
                return Make("Dreamcast", "disc image", "Sega Dreamcast GD-ROM/MIL-CD data track");
            if (Ascii(head, off, "SEGADISCSYSTEM"))
                return Make("Mega CD", "disc image", "Sega Mega-CD / Sega CD data track");
        }
        if (len >= 0x1FE10 && AllByte(head, 0x1FE00, 16, 0x55))
            return Make("VMU", "save card", "Dreamcast Visual Memory");

        // Sega Saturn backup memory — internal 32 KB or a backup cartridge — opens with the
        // repeated "BackUpRam Format" signature. A strong, low-false-positive string.
        if (Ascii(head, 0, "BackUpRam Format"))
            return Make("Saturn backup", "save card", "Sega Saturn backup RAM");

        // N64 Controller Pak (Mempak): exactly 32 KB with a checksum-valid header ID block.
        // Size alone is ambiguous with SRAM, so only the validated ID block claims it.
        if (len == DiscForge.Core.Saves.N64SaveReader.MempakSize &&
            DiscForge.Core.Saves.N64ControllerPak.IsControllerPak(head))
            return Make("N64 Controller Pak", "save card", "Nintendo 64 Controller Pak (Mempak)");

        // 3DO: the Opera filesystem volume header begins record-type 0x01 followed by
        // five 0x5A sync bytes ("\x01ZZZZZ") — a strong, fixed signature at the start of
        // the data track's user data (cooked 0, raw Mode 1 16, raw Mode 2 24).
        foreach (int off in new[] { 0, 16, 24 })
            if (head.Length > off + 6 && head[off] == 0x01 && AllByte(head, off + 1, 5, 0x5A))
                return Make("3DO", "disc image", "3DO Opera filesystem");

        // PC-Engine (TurboGrafx) CD and Neo-Geo CD carry a plaintext system string in the
        // boot area of the data track — a heuristic match (the string is specific enough
        // that a false positive is unlikely), scanned across the head.
        if (ContainsAscii(head, "PC Engine CD-ROM SYSTEM"))
            return Make("PC Engine CD", "disc image", "NEC PC Engine / TurboGrafx-CD");
        if (ContainsAscii(head, "NEO-GEO") || ContainsAscii(head, "NEOGEO"))
            return Make("Neo-Geo CD", "disc image", "SNK Neo-Geo CD");

        // ---- partition tables (weak; checked LATE so a filesystem wins first) ----
        // GPT: the header's "EFI PART" signature sits at LBA 1 (offset 0x200).
        if (len > 0x208 && Ascii(head, 0x200, "EFI PART"))
            return Make("GPT", "partition table", "GUID Partition Table");
        // MBR: the 0x55AA boot signature plus at least one plausible entry (a
        // non-zero type and sector count). This is a weak signal, so it runs after
        // every filesystem check above — a bare FAT12 floppy is claimed by its BPB.
        if (len >= 512 && head.Length > 0x1FF &&
            head[0x1FE] == 0x55 && head[0x1FF] == 0xAA &&
            HasPlausibleMbrEntry(head))
            return Make("MBR", "partition table", "MBR partitioned disk");

        // ---- weaker / tail signatures ----
        if (tail.Length == 12 && Ascii(tail, 0, "NER5")) return Make("NRG", "disc image", "Nero v2 (NER5)");
        if (tail.Length == 12 && Ascii(tail, 0, "NERO")) return Make("NRG", "disc image", "Nero v1 (NERO)");
        // DiscJuggler CDI: no header magic — identified by an 8-byte trailer at EOF whose
        // leading little-endian uint is the version (0x80000004 v2, 0x80000005 v3,
        // 0x80000006 v3.5). We already hold the last 12 bytes, so the trailer is tail[4..12];
        // the second word is the descriptor locator, which must point inside the file.
        if (tail.Length == 12)
        {
            uint cdiMagic = (uint)(tail[4] | (tail[5] << 8) | (tail[6] << 16) | (tail[7] << 24));
            uint cdiLocator = (uint)(tail[8] | (tail[9] << 8) | (tail[10] << 16) | (tail[11] << 24));
            if (cdiMagic is 0x80000004u or 0x80000005u or 0x80000006u && cdiLocator > 0 && cdiLocator < len)
            {
                string v = cdiMagic == 0x80000006u ? "3.5" : cdiMagic == 0x80000005u ? "3" : "2";
                return Make("CDI", "disc image", $"DiscJuggler (v{v})");
            }
        }
        if (len == 128 * 1024 && head.Length >= 2 && head[0] == (byte)'M' && head[1] == (byte)'C')
            return Make("PS1 memory card", "save card", ".mcr / .mcd");

        // ---- common (non-disc) formats -------------------------------------------
        // DiscForge can't act on these, but naming them beats "unrecognised". Checked
        // last, so every disc / retro format above wins first.

        // Video containers
        if (MagicAt(head, 4, (byte)'f', (byte)'t', (byte)'y', (byte)'p')) return Make("MP4 / MOV", "video", "ISO base-media (MP4/M4V/MOV/3GP)");
        if (MagicAt(head, 0, 0x1A, 0x45, 0xDF, 0xA3)) return Make("Matroska / WebM", "video", "EBML container");
        if (Ascii(head, 0, "RIFF") && Ascii(head, 8, "AVI ")) return Make("AVI", "video", "RIFF AVI");
        if (MagicAt(head, 0, 0x00, 0x00, 0x01, 0xBA)) return Make("MPEG-PS", "video", "MPEG program stream");
        if (Ascii(head, 0, "FLV")) return Make("FLV", "video", "Flash video");

        // Audio (non-game)
        if (Ascii(head, 0, "fLaC")) return Make("FLAC", "audio", "Free Lossless Audio Codec");
        if (Ascii(head, 0, "OggS")) return Make("Ogg", "audio", "Ogg (Vorbis/Opus/…)");
        if (Ascii(head, 0, "RIFF") && Ascii(head, 8, "WAVE")) return Make("WAV", "audio", "RIFF PCM audio");
        if (Ascii(head, 0, "FORM") && Ascii(head, 8, "AIFF")) return Make("AIFF", "audio", "Apple/SGI audio");
        if (Ascii(head, 0, "ID3")) return Make("MP3", "audio", "MPEG-1/2 Layer III (ID3)");

        // Images
        if (MagicAt(head, 0, 0x89, (byte)'P', (byte)'N', (byte)'G')) return Make("PNG", "image", "Portable Network Graphics");
        if (head.Length > 2 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return Make("JPEG", "image", "JFIF/EXIF image");
        if (Ascii(head, 0, "GIF87a") || Ascii(head, 0, "GIF89a")) return Make("GIF", "image", "Graphics Interchange Format");
        if (Ascii(head, 0, "RIFF") && Ascii(head, 8, "WEBP")) return Make("WebP", "image", "Google WebP");
        if (MagicAt(head, 0, 0x49, 0x49, 0x2A, 0x00) || MagicAt(head, 0, 0x4D, 0x4D, 0x00, 0x2A)) return Make("TIFF", "image", "Tagged Image File Format");
        if (len > 54 && head.Length > 1 && head[0] == (byte)'B' && head[1] == (byte)'M') return Make("BMP", "image", "Windows bitmap");

        // Archives / compressed
        if (MagicAt(head, 0, (byte)'P', (byte)'K', 0x03, 0x04) || MagicAt(head, 0, (byte)'P', (byte)'K', 0x05, 0x06))
            return Make("ZIP", "archive", "PKZIP (also .jar/.epub/.cbz)");
        if (MagicAt(head, 0, (byte)'7', (byte)'z', 0xBC, 0xAF)) return Make("7-Zip", "archive", "7z archive");
        if (Ascii(head, 0, "Rar!")) return Make("RAR", "archive", "WinRAR archive");
        if (head.Length > 1 && head[0] == 0x1F && head[1] == 0x8B) return Make("gzip", "archive", "gzip stream");
        if (Ascii(head, 0, "BZh")) return Make("bzip2", "archive", "bzip2 stream");
        if (MagicAt(head, 0, 0xFD, (byte)'7', (byte)'z', (byte)'X')) return Make("xz", "archive", "xz stream");
        if (MagicAt(head, 0, 0x28, 0xB5, 0x2F, 0xFD)) return Make("zstd", "archive", "Zstandard stream");
        if (head.Length > 0x106 && Ascii(head, 0x101, "ustar")) return Make("tar", "archive", "POSIX tar");

        // Documents
        if (Ascii(head, 0, "%PDF")) return Make("PDF", "document", "Portable Document Format");

        // A raw CD data track opens with the 12-byte sync 00 FF×10 00.
        if (head.Length >= 16 && head[0] == 0x00 && head[11] == 0x00 && AllByte(head, 1, 10, 0xFF))
            return Make("BIN", "disc image", "raw CD data track (2352-byte sectors)");

        // Text catalogues, XML, or just plain text.
        string? body = SniffText(head);
        if (body is not null)
        {
            if (body.Contains("<datafile", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("clrmamepro", StringComparison.OrdinalIgnoreCase))
                return Make("DAT", "dataset", "ROM catalogue (Logiqx / clrmamepro)");
            if (body.TrimStart().StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
                return Make("XML", "text", "XML document");
            return Make("Text", "text", "plain text");
        }

        // A raw CD-DA track has no sync — just a whole number of 2352-byte sectors. A
        // weak, last-resort signal, so it runs immediately before "unknown".
        if (len >= 2352 * 4 && len % 2352 == 0)
            return Make("BIN", "disc image", "raw CD track (2352-byte sectors)");

        return FormatId.Unknown;
    }

    // ---- helpers ------------------------------------------------------------

    private static FormatId Make(string name, string category, string detail) =>
        new() { Name = name, Category = category, Detail = detail };

    // Scan a bounded window [at, at+len) of the buffer for an ASCII substring —
    // for fixed-region fields (e.g. the 32-byte ISO system identifier) that hold
    // a marker somewhere within them rather than at a precise offset.
    private static bool ContainsAscii(byte[] data, int at, int len, string s)
    {
        if (at < 0 || len < 0 || at + len > data.Length) return false;
        var needle = System.Text.Encoding.ASCII.GetBytes(s);
        if (needle.Length == 0 || len < needle.Length) return false;
        int last = at + len - needle.Length;
        for (int i = at; i <= last; i++)
        {
            int j = 0;
            while (j < needle.Length && data[i + j] == needle[j]) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }

    // Scan the (bounded) head for an ASCII substring — for boot-area system strings
    // that are present but not at a fixed offset.
    private static bool ContainsAscii(byte[] data, string s)
    {
        var needle = System.Text.Encoding.ASCII.GetBytes(s);
        if (needle.Length == 0 || data.Length < needle.Length) return false;
        int last = data.Length - needle.Length;
        for (int i = 0; i <= last; i++)
        {
            int j = 0;
            while (j < needle.Length && data[i + j] == needle[j]) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }

    private static bool Ascii(byte[] data, int at, string s)
    {
        if (at < 0 || at + s.Length > data.Length) return false;
        for (int i = 0; i < s.Length; i++) if (data[at + i] != (byte)s[i]) return false;
        return true;
    }

    // A PSF version byte (offset 0x03) identifies the emulated system: 0x01 PSF1,
    // 0x02 PSF2, 0x11 SSF, 0x12 DSF, 0x21 USF, 0x22 GSF, 0x23 SNSF, 0x24 QSF, 0x41 APSF.
    private static bool IsKnownPsfVersion(byte v) =>
        v is 0x01 or 0x02 or 0x11 or 0x12 or 0x21 or 0x22 or 0x23 or 0x24 or 0x41;

    // A BDMV version number ("0100"/"0200"/"0300") sits right after the magic.
    private static bool BdmvVersion(byte[] d, int at) =>
        Ascii(d, at, "0100") || Ascii(d, at, "0200") || Ascii(d, at, "0300");

    // Byte offset of the C64 BAM (track 18, sector 0): tracks 1–17 hold 21 sectors
    // each, so 17 × 21 = 357 blocks precede it → 357 × 256.
    private static int D64BamOffset() => 357 * 256;

    // At least one of the four 16-byte MBR entries at 0x1BE has a non-zero type
    // and a non-zero little-endian sector count — enough to call it a real table.
    private static bool HasPlausibleMbrEntry(byte[] head)
    {
        if (head.Length < 512) return false;
        for (int i = 0; i < 4; i++)
        {
            int off = 0x1BE + i * 16;
            byte type = head[off + 4];
            uint count = (uint)(head[off + 12] | (head[off + 13] << 8) |
                                (head[off + 14] << 16) | (head[off + 15] << 24));
            if (type != 0 && count > 0) return true;
        }
        return false;
    }

    private static bool Magic4(byte[] d, byte a, byte b, byte c, byte e) =>
        d.Length >= 4 && d[0] == a && d[1] == b && d[2] == c && d[3] == e;

    // A four-byte (big-endian) magic word at a fixed offset.
    private static bool MagicAt(byte[] d, int at, byte a, byte b, byte c, byte e) =>
        at >= 0 && at + 4 <= d.Length && d[at] == a && d[at + 1] == b && d[at + 2] == c && d[at + 3] == e;

    private static bool AllByte(byte[] d, int at, int count, byte value)
    {
        if (at + count > d.Length) return false;
        for (int i = 0; i < count; i++) if (d[at + i] != value) return false;
        return true;
    }

    // The first line or so, if the head is printable ASCII text.
    private static string? SniffText(byte[] head)
    {
        int n = Math.Min(head.Length, 4096);
        for (int i = 0; i < n; i++)
        {
            byte b = head[i];
            if (b == 0) return null;
            if (b is not (0x09 or 0x0A or 0x0D) && (b < 0x20 || b > 0x7E)) return null;
        }
        return Encoding.ASCII.GetString(head, 0, n);
    }

    // A GDI starts with a track count on its own line, then "n lba type size file …".
    private static bool LooksLikeGdi(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return false;
        if (!int.TryParse(lines[0].Trim(), out int count) || count is < 1 or > 99) return false;
        var parts = lines[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 5 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _);
    }
}
