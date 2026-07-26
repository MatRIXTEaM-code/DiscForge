// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;

namespace DiscForge.Core.Media;

/// <summary>What a disc turned out to be, as far as the drive will say.</summary>
public sealed record MediaIdentity
{
    /// <summary>CD-R / CD-RW only. The ATIP lead-in start time as "97m26s66f" —
    /// this triple is the dye/stamper manufacturer's code.</summary>
    public string? AtipCode { get; init; }
    /// <summary>DVD/BD only. The media ID string from the lead-in or ADIP,
    /// e.g. "TYG03" or "MCC 03RG20".</summary>
    public string? MediaId { get; init; }
    /// <summary>Who made the blank, resolved from AtipCode or MediaId. Null when
    /// the code is real but not in the table — which is common and not an error.</summary>
    public string? Manufacturer { get; init; }

    public bool IsRewritable { get; init; }
    public double? CapacityMb { get; init; }
    /// <summary>Lead-out MSF for CD media, as (min, sec, frame).</summary>
    public (int Min, int Sec, int Frame)? LeadOut { get; init; }

    /// <summary>DVD book type: 0 = DVD-ROM, 2 = DVD-R, 9 = DVD+RW, 0xA = DVD+R…</summary>
    public int? BookType { get; init; }
    public string? BookTypeName { get; init; }
    public int? Layers { get; init; }
    /// <summary>True if the drive reports the disc carries CSS/CPRM. Reported
    /// only — DiscForge implements no circumvention of any kind.</summary>
    public bool? Encrypted { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// Parsers for the media-identification structures. Pure: they take the bytes a
/// drive returned and say what they mean, so every offset here is exercisable in
/// a unit test with a captured response.
/// </summary>
public static class MediaIdentityParser
{
    /// <summary>
    /// Parse a READ TOC/PMA/ATIP format 0100b response.
    ///
    /// Layout (MMC-5, after the 4-byte header):
    ///   +0  bit7 ITWP valid, bits 6-4 reference speed
    ///   +2  bit6 disc type (0 = CD-R, 1 = CD-RW), bits 5-3 sub-type
    ///   +4..+6   lead-in start time  M / S / F   ← the manufacturer code
    ///   +8..+10  last possible lead-out start   M / S / F
    /// Absolute offsets are therefore 4, 6, 8-10 and 12-14.
    /// </summary>
    public static MediaIdentity? ParseAtip(ReadOnlySpan<byte> response)
    {
        if (response.Length < 16) return null;

        // A pressed disc has no ATIP; some drives answer with zeros rather than a
        // check condition, so treat an all-zero descriptor as "not recordable".
        bool allZero = true;
        for (int i = 4; i < 16; i++) if (response[i] != 0) { allZero = false; break; }
        if (allZero) return null;

        bool rewritable = (response[6] & 0x40) != 0;

        int inMin = response[8], inSec = response[9], inFrame = response[10];
        int outMin = response[12], outSec = response[13], outFrame = response[14];

        string code = $"{inMin:00}m{inSec:00}s{inFrame:00}f";

        double? capacity = null;
        int outFrames = ((outMin * 60) + outSec) * 75 + outFrame;
        int usable = outFrames - 150;                 // the 2-second pregap
        if (usable > 0) capacity = usable * 2048.0 / (1024.0 * 1024.0);

        var notes = new List<string>();
        var maker = AtipManufacturers.Lookup(code);
        if (maker is null)
            notes.Add($"ATIP code {code} is not in the manufacturer table — the disc is " +
                      "readable and writable regardless.");

        return new MediaIdentity
        {
            AtipCode = code,
            Manufacturer = maker,
            IsRewritable = rewritable,
            CapacityMb = capacity,
            LeadOut = (outMin, outSec, outFrame),
            Notes = notes,
        };
    }

    /// <summary>
    /// Parse a READ DISC STRUCTURE format 0x00 (physical format information)
    /// response. After the 4-byte header:
    ///   +0  book type (bits 7-4), part version (bits 3-0)
    ///   +2  layer type / track path / number of layers
    ///   +4..+7   start physical sector of the data area
    ///   +8..+11  end physical sector of the data area
    ///   +0x15..  disc manufacturer ID (8 chars) then media type ID, on -R/-RW
    /// </summary>
    public static MediaIdentity? ParsePhysicalFormat(ReadOnlySpan<byte> response)
    {
        if (response.Length < 20) return null;

        int book = (response[4] >> 4) & 0x0F;
        int layers = ((response[6] >> 5) & 0x03) + 1;

        uint dataStart = (uint)((response[9] << 16) | (response[10] << 8) | response[11]);
        uint dataEnd = (uint)((response[13] << 16) | (response[14] << 8) | response[15]);

        double? capacity = null;
        if (dataEnd > dataStart)
            capacity = (dataEnd - dataStart + 1) * 2048.0 / (1024.0 * 1024.0);

        // On -R/-RW the manufacturer and media-type strings sit inside the same
        // descriptor. On +R/+RW they don't, and ADIP has to be asked separately.
        string? mediaId = null;
        if (response.Length >= 4 + 0x24)
            mediaId = PrintableRun(response.Slice(4 + 0x19, 0x0B));

        var notes = new List<string>();
        if (mediaId is null && book is 0x9 or 0xA)
            notes.Add("This is +R/+RW media: the media ID lives in ADIP, which many drives " +
                      "will not report.");

        return new MediaIdentity
        {
            MediaId = mediaId,
            Manufacturer = mediaId is null ? null : DvdMediaIds.Lookup(mediaId),
            IsRewritable = book is 0x3 or 0x9 or 0xD,
            BookType = book,
            BookTypeName = BookTypeName(book),
            Layers = layers,
            CapacityMb = capacity,
            Notes = notes,
        };
    }

    /// <summary>Pull an ASCII media ID out of an ADIP response. Offsets vary by
    /// format revision, so this scans for the longest printable run instead.</summary>
    public static string? ParseAdipMediaId(ReadOnlySpan<byte> response)
        => response.Length < 8 ? null : PrintableRun(response[4..]);

    public static string BookTypeName(int book) => book switch
    {
        0x0 => "DVD-ROM",
        0x1 => "DVD-RAM",
        0x2 => "DVD-R",
        0x3 => "DVD-RW",
        0x4 => "HD DVD-ROM",
        0x9 => "DVD+RW",
        0xA => "DVD+R",
        0xD => "DVD+RW DL",
        0xE => "DVD+R DL",
        _ => $"unknown (0x{book:X1})",
    };

    /// <summary>Longest run of printable ASCII, trimmed. Media IDs are short
    /// strings padded with spaces or nulls, so this finds them reliably.</summary>
    private static string? PrintableRun(ReadOnlySpan<byte> s)
    {
        var best = new StringBuilder();
        var cur = new StringBuilder();
        foreach (byte b in s)
        {
            if (b is >= 0x20 and < 0x7F) cur.Append((char)b);
            else { if (cur.Length > best.Length) { best.Clear(); best.Append(cur); } cur.Clear(); }
        }
        if (cur.Length > best.Length) { best.Clear(); best.Append(cur); }
        var t = best.ToString().Trim();
        return t.Length >= 3 ? t : null;
    }
}

/// <summary>
/// ATIP lead-in start times to dye/stamper manufacturers. Not exhaustive — no
/// public list is — so an unmatched code is reported as-is rather than as an
/// error. Contributions come from reading real discs.
/// </summary>
public static class AtipManufacturers
{
    private static readonly Dictionary<string, string> Table = new(StringComparer.Ordinal)
    {
        ["97m26s66f"] = "Taiyo Yuden",
        ["97m26s65f"] = "Taiyo Yuden (That's)",
        ["97m26s60f"] = "Taiyo Yuden",
        ["97m34s22f"] = "Mitsubishi Chemical / Verbatim",
        ["97m34s23f"] = "Mitsubishi Chemical / Verbatim",
        ["97m34s20f"] = "Mitsubishi Chemical / Verbatim",
        ["97m34s24f"] = "CMC Magnetics",
        ["97m27s24f"] = "CMC Magnetics",
        ["97m32s00f"] = "Ritek",
        ["97m31s00f"] = "Ritek",
        ["97m17s06f"] = "Ricoh",
        ["97m27s18f"] = "Prodisc",
        ["97m28s16f"] = "Gigastorage",
        ["97m15s00f"] = "Moser Baer India",
        ["97m24s00f"] = "SKC",
        ["97m22s60f"] = "Plasmon",
        ["97m18s10f"] = "Kodak",
        ["97m21s40f"] = "Mitsui / MAM-A",
        ["97m23s00f"] = "Pioneer",
        ["97m25s20f"] = "Fuji Photo Film",
        ["97m30s10f"] = "Infosmart",
        ["97m35s10f"] = "Optical Disc Manufacturing",
    };

    public static string? Lookup(string atipCode)
        => Table.TryGetValue(atipCode, out var v) ? v : null;

    public static int KnownCodes => Table.Count;
}

/// <summary>DVD media ID strings to manufacturers, same caveats as ATIP.</summary>
public static class DvdMediaIds
{
    private static readonly Dictionary<string, string> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TYG01"] = "Taiyo Yuden 4× DVD-R",
        ["TYG02"] = "Taiyo Yuden 8× DVD-R",
        ["TYG03"] = "Taiyo Yuden 16× DVD-R",
        ["TYG04"] = "Taiyo Yuden 24× DVD-R",
        ["YUDEN000T02"] = "Taiyo Yuden 8× DVD+R",
        ["YUDEN000T03"] = "Taiyo Yuden 16× DVD+R",
        ["MCC 01RG20"] = "Mitsubishi / Verbatim 4× DVD-R",
        ["MCC 02RG20"] = "Mitsubishi / Verbatim 8× DVD-R",
        ["MCC 03RG20"] = "Mitsubishi / Verbatim 16× DVD-R",
        ["MCC 004"] = "Mitsubishi / Verbatim 16× DVD+R",
        ["MCC 003"] = "Mitsubishi / Verbatim 8× DVD+R",
        ["MKM 001"] = "Mitsubishi / Verbatim DVD+R DL",
        ["MKM 003"] = "Mitsubishi / Verbatim 8× DVD+R DL",
        ["CMC MAG. AM3"] = "CMC Magnetics 16× DVD+R",
        ["CMC MAG-AF1"] = "CMC Magnetics 16× DVD-R",
        ["CMC MAG-AE1"] = "CMC Magnetics 8× DVD-R",
        ["RITEKF1"] = "Ritek 8× DVD+R",
        ["RITEKG05"] = "Ritek 8× DVD-R",
        ["RITEKM02"] = "Ritek DVD+R DL",
        ["PRODISCF02"] = "Prodisc 8× DVD+R",
        ["INFOME R20"] = "InfoMedia 16× DVD-R",
        ["OPTODISCR16"] = "Optodisc 16× DVD-R",
    };

    public static string? Lookup(string mediaId)
        => Table.TryGetValue(mediaId.Trim(), out var v) ? v : null;

    public static int KnownIds => Table.Count;
}