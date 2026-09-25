// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Audio;

namespace DiscForge.Core.Recovery;

/// <summary>
/// Breaks a multi-copy audio-track disagreement using AccurateRip as the trust anchor, instead of
/// falling back to an unconfirmable byte-per-byte majority vote (<see cref="MergeMethod.VoteBestEffort"/>).
///
/// A CD data sector carries its own EDC/ECC, so <see cref="ProvenanceMerge"/> can already prove a data
/// sector correct on its own. A CD audio sector carries no such check — nothing in the sector itself
/// says whether it is right. AccurateRip fills that gap, but only at the granularity it was designed
/// for: a whole track's checksum, checked against a value thousands of other people's drives agreed on.
/// So the tie-break this class performs is per <em>track</em>, not per sector: given several candidate
/// copies of one track, it asks "does any one of these, taken whole, match a known-good AccurateRip
/// checksum for this track?" If exactly one does (or one clearly outranks the others by database
/// confidence), that candidate is provably the correct track — not a vote, a match against an external,
/// independently-submitted reference. If none match, this returns no resolution and the caller falls
/// back to the existing per-sector vote, which is exactly what already happens today.
///
/// Pure arithmetic over already-read bytes plus an already-parsed database; no network, and it recovers
/// nothing a copy did not already hold — it only decides, among copies that already hold a track,
/// which one to trust.
/// </summary>
public static class AccurateRipTieBreaker
{
    /// <summary>The outcome of trying to resolve one track's disagreement. <see cref="SourceIndex"/> is
    /// null when no candidate matched the database — the caller should fall back to its normal merge
    /// logic for this track's sectors.</summary>
    public sealed record Resolution(int? SourceIndex, AccurateRip.TrackStatus Status, int Confidence);

    private static readonly Resolution NoMatch = new(null, AccurateRip.TrackStatus.NotFound, 0);

    /// <summary>
    /// Try to resolve one audio track by checking each candidate copy's whole-track AccurateRip
    /// checksum against <paramref name="database"/>. Candidates are given as full disc images sharing
    /// the same sector size and addressing; <paramref name="startSector"/>/<paramref name="endSectorInclusive"/>
    /// give the track's span within them. When more than one candidate matches, the one with the higher
    /// database confidence wins; on an exact tie, the earliest candidate (lowest index) is kept — the
    /// same "first on a tie" convention <see cref="ProvenanceMerge"/> already uses for its byte vote.
    /// </summary>
    /// <param name="images">Full candidate disc images (same length, same sector size).</param>
    /// <param name="startSector">The track's first sector (inclusive), addressed the same way as <paramref name="images"/>.</param>
    /// <param name="endSectorInclusive">The track's last sector (inclusive).</param>
    /// <param name="sectorSize">Bytes per sector — 2352 for raw CD audio.</param>
    /// <param name="isFirstTrack">True for the disc's first track (AccurateRip trims a 5-sector guard band there).</param>
    /// <param name="isLastTrack">True for the disc's last track (same guard-band trim at the far end).</param>
    /// <param name="database">Parsed AccurateRip records for this disc pressing (see <see cref="AccurateRipDatabase"/>).</param>
    /// <param name="trackIndex">This track's 0-based index into <c>database</c> entries' <c>TrackChecksums</c>
    /// (AccurateRip numbers audio tracks only, in disc order — matching how <see cref="AccurateRip.Verify"/> is used elsewhere).</param>
    public static Resolution Resolve(
        IReadOnlyList<byte[]> images, int startSector, int endSectorInclusive, int sectorSize,
        bool isFirstTrack, bool isLastTrack,
        IReadOnlyList<AccurateRip.DbEntry> database, int trackIndex)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(database);
        if (sectorSize <= 0) throw new ArgumentException("Sector size must be positive.", nameof(sectorSize));
        if (endSectorInclusive < startSector)
            throw new ArgumentException("End sector precedes start sector.", nameof(endSectorInclusive));
        if (trackIndex < 0) throw new ArgumentException("Track index cannot be negative.", nameof(trackIndex));
        if (database.Count == 0 || images.Count == 0) return NoMatch;

        long at = (long)startSector * sectorSize;
        long length = (long)(endSectorInclusive - startSector + 1) * sectorSize;

        int? bestSource = null;
        var bestStatus = AccurateRip.TrackStatus.NotFound;
        int bestConfidence = 0;

        for (int k = 0; k < images.Count; k++)
        {
            // A copy that doesn't even reach this track's byte span (short read, truncated
            // file) simply isn't a candidate — never crash the tie-break over one bad copy.
            if (at < 0 || at + length > images[k].LongLength) continue;

            var mine = AccurateRip.Compute(images[k].AsSpan((int)at, (int)length), isFirstTrack, isLastTrack);
            var (status, confidence) = MatchTrack(mine, database, trackIndex);
            if (status == AccurateRip.TrackStatus.NotFound) continue;

            if (bestSource is null || confidence > bestConfidence)
            {
                bestSource = k;
                bestStatus = status;
                bestConfidence = confidence;
            }
        }

        return bestSource is int s ? new Resolution(s, bestStatus, bestConfidence) : NoMatch;
    }

    /// <summary>The same match rule <see cref="AccurateRip.Verify"/> applies per track, but addressed
    /// directly at one track index instead of walking a whole computed-track list in lockstep with the
    /// database's own indexing — this is used to check a single candidate against one track in isolation.</summary>
    private static (AccurateRip.TrackStatus Status, int Confidence) MatchTrack(
        AccurateRip.TrackChecksum mine, IReadOnlyList<AccurateRip.DbEntry> database, int trackIndex)
    {
        int bestConfidence = 0;
        var status = AccurateRip.TrackStatus.NotFound;
        foreach (var e in database)
        {
            if (trackIndex >= e.TrackChecksums.Count) continue;
            uint dbSum = e.TrackChecksums[trackIndex];
            if (dbSum == mine.V2)
            {
                if (e.Confidence >= bestConfidence) { bestConfidence = e.Confidence; status = AccurateRip.TrackStatus.MatchV2; }
            }
            else if (dbSum == mine.V1)
            {
                if (e.Confidence >= bestConfidence) { bestConfidence = e.Confidence; status = AccurateRip.TrackStatus.MatchV1; }
            }
        }
        return (status, bestConfidence);
    }
}
