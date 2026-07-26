// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Audio;

/// <summary>
/// AccurateRip verification: computes the checksums that let a ripped audio CD
/// be checked against AccurateRip's public database, so you know a rip is
/// bit-perfect — matching what thousands of other people's drives read — rather
/// than merely internally consistent.
///
/// The maths here is the long-established AccurateRip algorithm (v1 and v2):
/// a position-weighted sum over each track's 16-bit stereo samples, with the
/// first and last few samples of the disc handled specially (the drive-offset
/// boundary). The disc identifier used for the lookup is derived from the TOC
/// (track offsets and lead-out), exactly as the database is keyed.
///
/// This class is pure arithmetic over PCM samples and TOC data — no network.
/// The database lookup itself (an HTTP fetch of the AccurateRip binary record)
/// is a separate step done by the caller/CLI, so the checksum logic is fully
/// unit-testable offline. Computing a checksum reveals nothing protected and
/// enables nothing but verification.
/// </summary>
public static class AccurateRip
{
    public const int SamplesPerSector = 588;   // CD audio: 588 stereo frames/sector
    public const int BytesPerSector = 2352;

    /// <summary>Both AccurateRip checksum variants for one track.</summary>
    public sealed record TrackChecksum
    {
        public required uint V1 { get; init; }
        public required uint V2 { get; init; }
    }

    /// <summary>
    /// Compute a track's AccurateRip v1 and v2 checksums from its PCM samples.
    /// Samples are little-endian 16-bit stereo interleaved (L,R,L,R…), i.e. the
    /// raw CD audio payload. <paramref name="isFirstTrack"/> and
    /// <paramref name="isLastTrack"/> control the disc-boundary special-casing.
    /// </summary>
    /// <param name="pcm">The track's audio bytes (multiple of 4).</param>
    public static TrackChecksum Compute(ReadOnlySpan<byte> pcm, bool isFirstTrack, bool isLastTrack)
    {
        int frameCount = pcm.Length / 4;   // one frame = 32-bit stereo sample

        // AccurateRip skips the first 5 sectors' worth of samples on track 1 and
        // the last 5 sectors on the last track (the offset guard band).
        int guard = 5 * SamplesPerSector;
        int startFrame = isFirstTrack ? guard : 0;
        int endFrame = isLastTrack ? frameCount - guard : frameCount;
        if (endFrame < startFrame) endFrame = startFrame;

        uint crc1 = 0;
        uint crc2 = 0;
        // Position weight is 1-based over the whole track (not the trimmed range).
        for (int i = startFrame; i < endFrame; i++)
        {
            uint sample = (uint)(pcm[i * 4]
                                 | (pcm[i * 4 + 1] << 8)
                                 | (pcm[i * 4 + 2] << 16)
                                 | (pcm[i * 4 + 3] << 24));
            uint multiplier = (uint)(i + 1);

            // v1: sum of sample * (index+1), 32-bit wrap.
            crc1 += sample * multiplier;

            // v2: sum of the high and low 32-bit halves of sample*(index+1).
            ulong product = (ulong)sample * multiplier;
            uint lo = (uint)(product & 0xFFFFFFFF);
            uint hi = (uint)(product >> 32);
            crc2 += lo;
            crc2 += hi;
        }

        return new TrackChecksum { V1 = crc1, V2 = crc2 };
    }

    /// <summary>
    /// Compute a track's checksum directly from its raw 2352-byte sectors, a
    /// convenience over <see cref="Compute"/> for sector-addressed sources.
    /// </summary>
    public static TrackChecksum ComputeFromSectors(
        ReadOnlySpan<byte> sectors, bool isFirstTrack, bool isLastTrack)
        => Compute(sectors, isFirstTrack, isLastTrack);

    /// <summary>
    /// The AccurateRip disc IDs used to key the database. Returns the three IDs
    /// the service expects: two TOC-derived 32-bit ids and the FreeDB-style id.
    /// <paramref name="trackOffsetsLba"/> are each track's start LBA; the last
    /// element is the lead-out LBA.
    /// </summary>
    public static (uint Id1, uint Id2, uint CddbId) DiscIds(IReadOnlyList<int> trackOffsetsLba)
    {
        if (trackOffsetsLba.Count < 2)
            throw new ArgumentException("Need at least one track offset plus lead-out.");

        int trackCount = trackOffsetsLba.Count - 1;
        uint id1 = 0;
        uint id2 = 0;

        for (int i = 0; i < trackOffsetsLba.Count; i++)
        {
            uint offset = (uint)trackOffsetsLba[i];
            id1 += offset;
            id2 += (offset == 0 ? 1u : offset) * (uint)(i + 1);
        }

        uint cddb = CddbDiscId(trackOffsetsLba);
        return (id1, id2, cddb);
    }

    // FreeDB/CDDB disc id: sum of digit-sums of each track's start second,
    // combined with total seconds and track count.
    private static uint CddbDiscId(IReadOnlyList<int> offsetsLba)
    {
        int trackCount = offsetsLba.Count - 1;
        int n = 0;
        for (int i = 0; i < trackCount; i++)
        {
            int seconds = (offsetsLba[i] + 150) / 75;   // LBA→seconds (2s lead-in)
            n += DigitSum(seconds);
        }
        int leadoutSec = (offsetsLba[^1] + 150) / 75;
        int firstSec = (offsetsLba[0] + 150) / 75;
        int total = leadoutSec - firstSec;
        return (uint)(((n % 0xFF) << 24) | (total << 8) | trackCount);
    }

    private static int DigitSum(int n)
    {
        int s = 0;
        while (n > 0) { s += n % 10; n /= 10; }
        return s;
    }

    /// <summary>
    /// Compare computed checksums against a set of database entries and report,
    /// per track, whether it matched (v1 or v2) and with what confidence. The
    /// caller supplies the parsed database records for the disc.
    /// </summary>
    public static VerifyResult Verify(
        IReadOnlyList<TrackChecksum> computed,
        IReadOnlyList<DbEntry> database)
    {
        var tracks = new List<TrackVerdict>(computed.Count);
        for (int i = 0; i < computed.Count; i++)
        {
            var mine = computed[i];
            int bestConfidence = 0;
            var status = TrackStatus.NotFound;

            foreach (var e in database)
            {
                if (i >= e.TrackChecksums.Count) continue;
                uint dbSum = e.TrackChecksums[i];
                if (dbSum == mine.V2)
                {
                    if (e.Confidence >= bestConfidence) { bestConfidence = e.Confidence; status = TrackStatus.MatchV2; }
                }
                else if (dbSum == mine.V1)
                {
                    if (e.Confidence >= bestConfidence) { bestConfidence = e.Confidence; status = TrackStatus.MatchV1; }
                }
            }

            tracks.Add(new TrackVerdict
            {
                TrackIndex = i, Status = status, Confidence = bestConfidence,
                ComputedV1 = mine.V1, ComputedV2 = mine.V2,
            });
        }
        return new VerifyResult { Tracks = tracks };
    }

    /// <summary>A parsed AccurateRip database record (one submission).</summary>
    public sealed record DbEntry
    {
        public required int Confidence { get; init; }
        /// <summary>Per-track checksum from the database, in track order.</summary>
        public required IReadOnlyList<uint> TrackChecksums { get; init; }
    }

    public enum TrackStatus { NotFound, MatchV1, MatchV2 }

    public sealed record TrackVerdict
    {
        public required int TrackIndex { get; init; }
        public required TrackStatus Status { get; init; }
        public required int Confidence { get; init; }
        public required uint ComputedV1 { get; init; }
        public required uint ComputedV2 { get; init; }
        public bool Accurate => Status != TrackStatus.NotFound;
    }

    public sealed record VerifyResult
    {
        public required IReadOnlyList<TrackVerdict> Tracks { get; init; }
        public bool AllAccurate => Tracks.Count > 0 && Tracks.All(t => t.Accurate);
        public int AccurateCount => Tracks.Count(t => t.Accurate);
        public string Summary => Tracks.Count == 0
            ? "No tracks to verify."
            : AllAccurate
                ? $"All {Tracks.Count} track(s) verified accurate against AccurateRip."
                : $"{AccurateCount}/{Tracks.Count} track(s) verified accurate; " +
                  $"{Tracks.Count - AccurateCount} not found or mismatched.";
    }
}
