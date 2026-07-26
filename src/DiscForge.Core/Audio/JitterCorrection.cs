// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Audio;

/// <summary>
/// Digital Audio Extraction jitter correction.
///
/// CD-DA sectors carry **no header**. A data sector announces its own address;
/// an audio sector does not. So when a drive is asked for sector N it may hand
/// back audio that actually begins a few samples either side of N, and the error
/// varies between reads. Concatenating chunks blindly then yields clicks, or
/// silently drifts.
///
/// The fix, as used by cdparanoia and EAC: read chunks that **overlap**, then
/// find where the new chunk truly lines up against the tail of the previous one,
/// and stitch at that point rather than where the drive claimed.
///
/// Pure and fully testable — this is arithmetic over sample data, no hardware.
/// A drive with "accurate stream" returns offset 0 every time, and the correction
/// costs only the overlap re-read.
/// </summary>
public static class JitterCorrection
{
    /// <summary>16-bit stereo: 2 channels x 2 bytes.</summary>
    public const int BytesPerSample = 4;

    /// <summary>Typical drive jitter is a handful of samples; this is generous.</summary>
    public const int DefaultMaxOffsetSamples = 32;

    /// <summary>Smallest overlap that leaves a usable comparison window.</summary>
    public static int MinimumOverlapSamples(int maxOffsetSamples = DefaultMaxOffsetSamples)
        => 2 * maxOffsetSamples + 64;

    /// <summary>Result of aligning a chunk against the previous one.</summary>
    /// <param name="OffsetSamples">
    /// The jitter the DRIVE applied: positive means it returned audio from this
    /// many samples LATER than asked for. 0 means it gave exactly what was asked.
    /// </param>
    /// <param name="Confident">
    /// True when exactly one alignment matched. If several match — which happens
    /// in silence, where every offset looks identical — the offset is not
    /// trustworthy and the caller should keep the drive's own positioning.
    /// </param>
    public readonly record struct Alignment(int OffsetSamples, bool Confident)
    {
        public int OffsetBytes => OffsetSamples * BytesPerSample;
        public static Alignment None => new(0, true);
    }

    /// <summary>
    /// Find where <paramref name="candidate"/> lines up against
    /// <paramref name="reference"/>. Both must cover the same region of the disc:
    /// reference is the tail of the previous read, candidate the head of the next.
    /// </summary>
    /// <param name="maxOffsetSamples">Search window, in samples, either side.</param>
    public static Alignment Align(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> candidate,
                                  int maxOffsetSamples = DefaultMaxOffsetSamples)
    {
        if (maxOffsetSamples < 0) throw new ArgumentOutOfRangeException(nameof(maxOffsetSamples));
        if (reference.Length % BytesPerSample != 0 || candidate.Length % BytesPerSample != 0)
            throw new ArgumentException("Audio buffers must be a whole number of 4-byte samples.");

        int refSamples = reference.Length / BytesPerSample;
        int candSamples = candidate.Length / BytesPerSample;
        if (refSamples == 0 || candSamples == 0) return Alignment.None;

        // The window must leave slack on BOTH sides: sliding the candidate
        // +/- maxOffset costs 2x maxOffset of the shorter buffer. Taking only
        // one maxOffset (the obvious mistake) leaves negative jitter untestable,
        // so it silently never gets detected.
        int window = Math.Min(refSamples, candSamples) - 2 * maxOffsetSamples;
        if (window <= 0) return new Alignment(0, Confident: false);

        // Silence (or any constant) matches at every offset — an offset derived
        // from it would be a guess. Say so rather than inventing one.
        if (IsUniform(reference)) return new Alignment(0, Confident: false);

        int found = 0;
        int matches = 0;

        // If candidate[i] = disc[start + jitter + i] and reference[i] = disc[start + i],
        // they agree when refIndex = jitter + candIndex. So fix the reference
        // window and slide the candidate to candStart = refStart - jitter.
        int refStart = maxOffsetSamples;
        for (int jitter = -maxOffsetSamples; jitter <= maxOffsetSamples; jitter++)
        {
            int candStart = refStart - jitter;
            if (candStart < 0 || candStart + window > candSamples) continue;

            if (Equal(reference.Slice(refStart * BytesPerSample, window * BytesPerSample),
                      candidate.Slice(candStart * BytesPerSample, window * BytesPerSample)))
            {
                matches++;
                if (matches == 1) found = jitter;
                else return new Alignment(0, Confident: false);   // ambiguous
            }
        }

        return matches == 1 ? new Alignment(found, Confident: true)
                            : new Alignment(0, Confident: false);
    }

    /// <summary>
    /// Stitch a new chunk onto what's already been collected, using the overlap
    /// to find the true join. Returns the bytes of <paramref name="chunk"/> that
    /// are genuinely new.
    /// </summary>
    /// <param name="previousTail">The last <c>overlapBytes</c> already accepted.</param>
    /// <param name="chunk">The freshly read chunk, starting at the overlap.</param>
    /// <param name="overlapBytes">How much of <paramref name="chunk"/> overlaps.</param>
    /// <param name="alignment">Where the chunk actually lined up.</param>
    public static ReadOnlySpan<byte> NewBytes(ReadOnlySpan<byte> previousTail, ReadOnlySpan<byte> chunk,
                                              int overlapBytes, out Alignment alignment)
    {
        alignment = Align(previousTail, chunk[..Math.Min(overlapBytes, chunk.Length)]);

        // The chunk was asked for at (position - overlap) but actually starts at
        // (position - overlap + jitter). The audio we still need begins at
        // `position`, i.e. (overlap - jitter) samples into the chunk.
        int skip = overlapBytes - alignment.OffsetBytes;
        skip = Math.Clamp(skip, 0, chunk.Length);
        return chunk[skip..];
    }

    private static bool Equal(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceEqual(b);

    /// <summary>True if every sample is identical — silence, or a constant tone
    /// so pure it carries no positional information.</summary>
    private static bool IsUniform(ReadOnlySpan<byte> audio)
    {
        if (audio.Length < BytesPerSample * 2) return true;
        var first = audio[..BytesPerSample];
        for (int i = BytesPerSample; i + BytesPerSample <= audio.Length; i += BytesPerSample)
            if (!audio.Slice(i, BytesPerSample).SequenceEqual(first)) return false;
        return true;
    }
}
