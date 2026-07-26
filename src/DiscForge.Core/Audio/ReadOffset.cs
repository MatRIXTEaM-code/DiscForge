// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Audio;

/// <summary>
/// The read-offset arithmetic a Redump-grade audio dump needs: converting between
/// samples, bytes and CD frames (sectors), combining a drive's read offset with a
/// pressing's write offset, and applying a signed sample offset to a PCM buffer so
/// the samples line up with the disc's logical position.
///
/// Redump keys every dump to a <b>combined read offset</b> — the drive's own read
/// offset (from the community drive-offset table) plus the pressing's write offset
/// — expressed in stereo samples. Correcting a rip means sliding the whole PCM
/// stream by that many samples, reading a little past the start and end of the disc
/// (the "overread" guard band) to fill what the slide exposes. This class is the
/// pure, unit-testable arithmetic; the physical over-reading of the guard band lives
/// in the Windows drive layer (see docs/REDUMP_PHYSICAL.md).
///
/// Clean-room: this is sample bookkeeping on already-read Red Book audio. It reads
/// and repositions PCM; it does not defeat any protection.
/// </summary>
public static class ReadOffset
{
    /// <summary>Bytes per stereo 16-bit sample frame (L+R). CD audio is 16-bit/2ch.</summary>
    public const int BytesPerSample = 4;

    /// <summary>Stereo samples in one 2352-byte CD-DA sector.</summary>
    public const int SamplesPerSector = 588;

    /// <summary>Bytes in one CD-DA sector.</summary>
    public const int BytesPerSector = SamplesPerSector * BytesPerSample;   // 2352

    /// <summary>Samples → bytes.</summary>
    public static int SamplesToBytes(int samples) => checked(samples * BytesPerSample);

    /// <summary>Bytes → whole samples. Throws if <paramref name="bytes"/> is not a whole number of samples.</summary>
    public static int BytesToSamples(int bytes)
    {
        if (bytes % BytesPerSample != 0)
            throw new ArgumentException($"{bytes} bytes is not a whole number of {BytesPerSample}-byte samples.", nameof(bytes));
        return bytes / BytesPerSample;
    }

    /// <summary>
    /// The combined read offset Redump records: the drive's read offset plus the
    /// pressing's write offset, both in samples. Both may be negative.
    /// </summary>
    public static int Combine(int driveReadOffsetSamples, int discWriteOffsetSamples)
        => checked(driveReadOffsetSamples + discWriteOffsetSamples);

    /// <summary>
    /// How many extra sectors must be over-read to supply the samples a shift of
    /// <paramref name="offsetSamples"/> exposes at the disc edge — i.e.
    /// ceil(|offset| / 588). Zero when the offset is zero.
    /// </summary>
    public static int OverreadSectors(int offsetSamples)
    {
        int abs = Math.Abs(offsetSamples);
        return (abs + SamplesPerSector - 1) / SamplesPerSector;
    }

    /// <summary>
    /// Apply a signed sample offset to a PCM buffer, returning a new buffer of the
    /// SAME length. A <b>positive</b> offset slides the audio earlier (drops the
    /// first <paramref name="offsetSamples"/> samples and pads that many silent
    /// samples onto the end); a <b>negative</b> offset slides it later (pads silence
    /// at the front). Formally: <c>output[i] = input[i + offsetSamples]</c>, with
    /// out-of-range positions filled with silence (zero).
    ///
    /// In practice a real dumper does not pad with silence — it over-reads the guard
    /// band so the exposed samples are the disc's true neighbours. This in-memory
    /// form is what you use when the guard band isn't available and for verifying the
    /// arithmetic; see <see cref="OverreadSectors"/> for how much to over-read.
    /// </summary>
    public static byte[] Apply(ReadOnlySpan<byte> pcm, int offsetSamples)
    {
        if (pcm.Length % BytesPerSample != 0)
            throw new ArgumentException("PCM length must be a whole number of 4-byte samples.", nameof(pcm));

        int totalSamples = pcm.Length / BytesPerSample;
        var output = new byte[pcm.Length];
        if (offsetSamples == 0) { pcm.CopyTo(output); return output; }

        for (int i = 0; i < totalSamples; i++)
        {
            int src = i + offsetSamples;
            if (src < 0 || src >= totalSamples) continue;   // leave silent
            pcm.Slice(src * BytesPerSample, BytesPerSample)
               .CopyTo(output.AsSpan(i * BytesPerSample, BytesPerSample));
        }
        return output;
    }

    /// <summary>
    /// Whether applying <paramref name="offsetSamples"/> to <paramref name="pcm"/>
    /// would discard only silence. A positive offset drops the leading
    /// <c>offsetSamples</c> samples; a negative offset drops the trailing
    /// <c>|offsetSamples|</c>. If that discarded edge is not silent, the offset may be
    /// wrong or the disc has audio right up to the edge — a signal to over-read the
    /// real guard band rather than trust an in-memory shift.
    /// </summary>
    public static bool ShiftDiscardsOnlySilence(ReadOnlySpan<byte> pcm, int offsetSamples)
    {
        if (offsetSamples == 0) return true;
        int dropSamples = Math.Min(Math.Abs(offsetSamples), pcm.Length / BytesPerSample);
        int dropBytes = dropSamples * BytesPerSample;
        var edge = offsetSamples > 0 ? pcm[..dropBytes] : pcm[^dropBytes..];
        return Silence.IsSilent(edge);
    }
}

/// <summary>
/// Silence and level analysis over 16-bit little-endian PCM. Redump dumps lean on
/// this to locate the silent guard band at the disc edges, to sanity-check an
/// applied read offset, and to describe pregap/lead-in content. Pure and testable.
/// </summary>
public static class Silence
{
    /// <summary>True when every 16-bit sample in the span is zero. An empty span is silent.</summary>
    public static bool IsSilent(ReadOnlySpan<byte> pcm)
    {
        for (int i = 0; i < pcm.Length; i++)
            if (pcm[i] != 0) return false;
        return true;
    }

    /// <summary>
    /// Count of leading whole stereo samples (4 bytes) that are entirely zero, from
    /// the start of the buffer. Counts samples, not bytes.
    /// </summary>
    public static int LeadingSilenceSamples(ReadOnlySpan<byte> pcm)
    {
        int total = pcm.Length / ReadOffset.BytesPerSample;
        int n = 0;
        while (n < total && SampleIsZero(pcm, n)) n++;
        return n;
    }

    /// <summary>Count of trailing zero stereo samples from the end of the buffer.</summary>
    public static int TrailingSilenceSamples(ReadOnlySpan<byte> pcm)
    {
        int total = pcm.Length / ReadOffset.BytesPerSample;
        int n = 0;
        while (n < total && SampleIsZero(pcm, total - 1 - n)) n++;
        return n;
    }

    /// <summary>
    /// The peak absolute 16-bit sample value across both channels (0…32768). Zero
    /// means digital silence. Useful as a one-number "is there anything here" gauge.
    /// </summary>
    public static int Peak(ReadOnlySpan<byte> pcm)
    {
        int peak = 0;
        int usable = pcm.Length - (pcm.Length % 2);
        for (int i = 0; i < usable; i += 2)
        {
            short s = (short)(pcm[i] | (pcm[i + 1] << 8));
            int mag = s == short.MinValue ? 32768 : Math.Abs((int)s);
            if (mag > peak) peak = mag;
        }
        return peak;
    }

    private static bool SampleIsZero(ReadOnlySpan<byte> pcm, int sampleIndex)
    {
        int at = sampleIndex * ReadOffset.BytesPerSample;
        return pcm[at] == 0 && pcm[at + 1] == 0 && pcm[at + 2] == 0 && pcm[at + 3] == 0;
    }
}
