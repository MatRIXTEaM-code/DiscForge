// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Convert;

/// <summary>The result of checking whether a format conversion preserved the disc byte-for-byte.</summary>
public sealed record ConvVerifyResult
{
    public required bool Lossless { get; init; }
    public required long LengthA { get; init; }
    public required long LengthB { get; init; }
    public required int SectorSize { get; init; }
    /// <summary>Byte offset of the first difference when the two are the same length but diverge; null otherwise.</summary>
    public long? FirstDiffOffset { get; init; }

    public string Summary()
    {
        if (Lossless) return $"LOSSLESS — both decode to the same {LengthA:N0} bytes ({LengthA / SectorSize:N0} sectors); the conversion preserved every byte.";
        if (LengthA != LengthB)
        {
            long delta = LengthB - LengthA;
            long secDelta = SectorSize > 0 && delta % SectorSize == 0 ? delta / SectorSize : 0;
            string sec = secDelta != 0 ? $" ({secDelta:+#;-#;0} sector(s))" : "";
            return $"NOT LOSSLESS — sizes differ: A={LengthA:N0}, B={LengthB:N0} bytes ({delta:+#;-#;0}{sec}). " +
                   "The conversion added or dropped data (e.g. a track, padding, or subchannel).";
        }
        long off = FirstDiffOffset ?? 0;
        long sector = SectorSize > 0 ? off / SectorSize : 0;
        long within = SectorSize > 0 ? off % SectorSize : off;
        return $"NOT LOSSLESS — same size but the bytes differ, first at offset {off:N0} (sector {sector:N0}, +{within} within it). " +
               "The two images are not the same disc data.";
    }
}

/// <summary>
/// verify-convert — prove a format conversion kept the disc byte-for-byte. People routinely convert bin/cue to
/// CHD (or back) and have no way to confirm nothing was lost; a mismatch in track split, padding or subchannel
/// silently changes the data. This decodes BOTH images to their raw sector bytes and compares them exactly,
/// reporting LOSSLESS or the precise divergence — a size delta (a dropped/added track or subchannel) or the first
/// differing sector. Read-only comparison; it converts nothing itself.
/// </summary>
public static class ConversionVerify
{
    public const int CdSector = 2352;

    public static ConvVerifyResult Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int sectorSize = CdSector)
    {
        if (sectorSize <= 0) throw new ArgumentException("Sector size must be positive.", nameof(sectorSize));

        if (a.Length != b.Length)
            return new ConvVerifyResult { Lossless = false, LengthA = a.Length, LengthB = b.Length, SectorSize = sectorSize };

        int firstDiff = -1;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) { firstDiff = i; break; }

        return new ConvVerifyResult
        {
            Lossless = firstDiff < 0,
            LengthA = a.Length,
            LengthB = b.Length,
            SectorSize = sectorSize,
            FirstDiffOffset = firstDiff < 0 ? null : firstDiff,
        };
    }
}
