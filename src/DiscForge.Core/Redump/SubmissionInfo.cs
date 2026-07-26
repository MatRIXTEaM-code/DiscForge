// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Security.Cryptography;
using System.Text;
using DiscForge.Core.Cue;
using DiscForge.Core.Patch;
using DiscForge.Core.Raw;

namespace DiscForge.Core.Redump;

/// <summary>Per-track checksums and layout for a submission.</summary>
public sealed record TrackSubmission
{
    public required int Number { get; init; }
    public required CueTrackType Type { get; init; }
    public required long Size { get; init; }
    public required long Sectors { get; init; }
    public required string Crc32 { get; init; }
    public required string Md5 { get; init; }
    public required string Sha1 { get; init; }
}

/// <summary>
/// The auto-computable half of a redump.org submission: for a dump in any container
/// DiscForge can read (bin/cue, CHD, CDI, NRG, ISO, …, via the conversion hub), the
/// per-track and whole-disc CRC-32 / MD5 / SHA-1, sizes and track layout, the cuesheet,
/// and — when a raw sub-channel sidecar is present — a LibCrypt/sub-channel summary.
///
/// This is deliberately the SOFTWARE half. The physical fields a submission also needs —
/// the dumping drive, its read/write offset, the disc's mould/ring codes, the dump date —
/// come from the rip itself and cannot be derived from the image, so they are listed as
/// blanks for the submitter to fill. Nothing here is protection-related; the checksums
/// are exactly what redump.org verifies a preserved dump against.
/// </summary>
public sealed record SubmissionInfo
{
    public required string FileName { get; init; }
    public required string InputFormat { get; init; }
    public required IReadOnlyList<TrackSubmission> Tracks { get; init; }
    public required long TotalSize { get; init; }
    /// <summary>Combined hash over every track's data, in order (the whole-image hash).</summary>
    public required string CombinedCrc32 { get; init; }
    public required string CombinedMd5 { get; init; }
    public required string CombinedSha1 { get; init; }
    /// <summary>The cuesheet text — read verbatim from a .cue input, else synthesised.</summary>
    public required string Cuesheet { get; init; }
    /// <summary>A sub-channel/LibCrypt summary when a .sub sidecar was found; else null.</summary>
    public string? SubchannelSummary { get; init; }
    public bool HasLibCrypt { get; init; }

    /// <summary>Render the redump.org-style submission text (auto fields filled,
    /// physical fields left blank for the submitter).</summary>
    public string ToRedumpText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Common Disc Info:");
        sb.AppendLine("\tTitle: ");
        sb.AppendLine("\tRegion: ");
        sb.AppendLine("\tBarcode: ");
        sb.AppendLine();
        sb.AppendLine("Ringcode / Mastering (from the physical disc — not derivable from the image):");
        sb.AppendLine("\tMould SID / Mastering code: ");
        sb.AppendLine();
        sb.AppendLine("Dumping Info (from the rip — not derivable from the image):");
        sb.AppendLine("\tDrive / model: ");
        sb.AppendLine("\tRead/write offset: ");
        sb.AppendLine("\tDump date: ");
        sb.AppendLine();
        sb.AppendLine($"Format: {InputFormat}");
        sb.AppendLine($"Total size: {TotalSize:N0} bytes");
        if (SubchannelSummary is not null)
            sb.AppendLine($"Sub-channel: {SubchannelSummary}" + (HasLibCrypt ? "  (LibCrypt protection detected)" : ""));
        sb.AppendLine();
        sb.AppendLine("Size & Checksums (per track):");
        foreach (var t in Tracks)
        {
            sb.AppendLine($"\tTrack {t.Number} [{TypeLabel(t.Type)}]  " +
                          $"{t.Sectors:N0} sectors, {t.Size:N0} bytes");
            sb.AppendLine($"\t\tCRC32: {t.Crc32}   MD5: {t.Md5}");
            sb.AppendLine($"\t\tSHA1:  {t.Sha1}");
        }
        sb.AppendLine();
        sb.AppendLine("Whole image:");
        sb.AppendLine($"\tCRC32: {CombinedCrc32}   MD5: {CombinedMd5}");
        sb.AppendLine($"\tSHA1:  {CombinedSha1}");
        sb.AppendLine();
        sb.AppendLine("Cuesheet:");
        foreach (var line in Cuesheet.Replace("\r\n", "\n").Split('\n'))
            sb.AppendLine("\t" + line);
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    internal static string TypeLabel(CueTrackType t) => t switch
    {
        CueTrackType.Audio => "AUDIO",
        CueTrackType.Mode1_2048 => "MODE1/2048",
        CueTrackType.Mode1_2352 => "MODE1/2352",
        CueTrackType.Mode2_2336 => "MODE2/2336",
        CueTrackType.Mode2_2352 => "MODE2/2352",
        _ => t.ToString().ToUpperInvariant(),
    };
}

/// <summary>Builds a <see cref="SubmissionInfo"/> from a dump on disk.</summary>
public static class SubmissionInfoGenerator
{
    /// <summary>
    /// Generate submission info for the image at <paramref name="path"/>. The image is
    /// read through the universal conversion hub, so any format DiscForge can read is
    /// accepted; the cuesheet is taken verbatim when the input is a .cue, otherwise a
    /// minimal one is synthesised from the track layout. A raw sub-channel sidecar named
    /// <c>&lt;base&gt;.sub</c> next to the input, when present, is analysed for LibCrypt.
    /// </summary>
    public static SubmissionInfo Generate(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Image not found.", path);

        var model = DiscForge.Core.Convert.DiscConverter.Read(path);

        var tracks = new List<TrackSubmission>(model.Tracks.Count);
        long total = 0;
        using var cCrc = new Crc32Accumulator();
        using var cMd5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var cSha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        foreach (var t in model.Tracks)
        {
            byte[] d = t.Data;
            total += d.Length;
            cCrc.Append(d); cMd5.AppendData(d); cSha1.AppendData(d);
            tracks.Add(new TrackSubmission
            {
                Number = t.Number,
                Type = t.Type,
                Size = d.Length,
                Sectors = t.SectorSize > 0 ? d.Length / t.SectorSize : 0,
                Crc32 = BpsPatch.Crc32(d).ToString("x8"),
                Md5 = Hex(MD5.HashData(d)),
                Sha1 = Hex(SHA1.HashData(d)),
            });
        }

        string cuesheet;
        if (Path.GetExtension(path).Equals(".cue", StringComparison.OrdinalIgnoreCase))
            cuesheet = File.ReadAllText(path);
        else
            cuesheet = SynthesiseCue(model, Path.GetFileNameWithoutExtension(path));

        string? subSummary = null;
        bool libCrypt = false;
        string subPath = Path.ChangeExtension(path, ".sub");
        if (File.Exists(subPath))
        {
            try
            {
                using var ss = File.OpenRead(subPath);
                var an = RawSubchannel.Analyse(ss);
                subSummary = an.Summary;
                libCrypt = an.LooksLikeLibCrypt;
            }
            catch { /* an unreadable sidecar is simply omitted */ }
        }

        return new SubmissionInfo
        {
            FileName = Path.GetFileName(path),
            InputFormat = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
            Tracks = tracks,
            TotalSize = total,
            CombinedCrc32 = cCrc.Finish().ToString("x8"),
            CombinedMd5 = Hex(cMd5.GetHashAndReset()),
            CombinedSha1 = Hex(cSha1.GetHashAndReset()),
            Cuesheet = cuesheet,
            SubchannelSummary = subSummary,
            HasLibCrypt = libCrypt,
        };
    }

    // A minimal cuesheet from the track layout, for non-.cue inputs.
    private static string SynthesiseCue(DiscForge.Core.Convert.DiscModel model, string baseName)
    {
        var sb = new StringBuilder();
        sb.Append("FILE \"").Append(baseName).Append(".bin\" BINARY\n");
        foreach (var t in model.Tracks)
        {
            sb.Append($"  TRACK {t.Number:D2} {SubmissionInfo.TypeLabel(t.Type)}\n");
            if (t.PregapSectors > 0) sb.Append($"    PREGAP {Msf(t.PregapSectors)}\n");
            sb.Append("    INDEX 01 00:00:00\n");
        }
        return sb.ToString();
    }

    private static string Msf(int sectors)
    {
        int m = sectors / (60 * 75), s = sectors / 75 % 60, f = sectors % 75;
        return $"{m:D2}:{s:D2}:{f:D2}";
    }

    private static string Hex(byte[] b) => System.Convert.ToHexString(b).ToLowerInvariant();

    // Incremental CRC-32 (zlib polynomial) for the whole-image combined checksum.
    private sealed class Crc32Accumulator : IDisposable
    {
        private static readonly uint[] Table = Build();
        private uint _c = 0xFFFFFFFF;
        public void Append(ReadOnlySpan<byte> data)
        {
            foreach (byte b in data) _c = Table[(_c ^ b) & 0xFF] ^ (_c >> 8);
        }
        public uint Finish() => _c ^ 0xFFFFFFFF;
        public void Dispose() { }
        private static uint[] Build()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }
    }
}
