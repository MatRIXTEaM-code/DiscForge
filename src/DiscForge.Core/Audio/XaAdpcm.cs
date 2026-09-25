// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Audio;

/// <summary>
/// Decodes CD-ROM XA ADPCM audio — the compressed streamed audio a PlayStation
/// game plays from disc, and what "Playstation XA Copier" / "BGM 2 WAV" pull out.
/// This is a plain audio codec, not protection: it decodes a person's own game's
/// audio to PCM. Clean-room, implemented from the public XA ADPCM description
/// (nocash psx-spx / the public "XA ADPCM documentation"), not from any GPL
/// decoder.
///
/// Layout, from that description:
///   The audio data area is 2304 bytes = 18 sound groups of 128 bytes.
///   Each 128-byte group:
///     bytes  0-15  sound parameters: SP0-3, then SP0-3 again, SP4-7, SP4-7 again.
///                  Each byte = filter (bits 7-4) and range/shift (bits 3-0).
///     bytes 16-127 112 data bytes = 28 four-byte words. In 4-bit mode each byte
///                  holds two sound units (low nibble = even unit, high = odd),
///                  and word index i (0-27) is the sample index within each unit.
///   Decode a nibble:  sample = signext4(nibble) &lt;&lt; (12 - range)
///                              + (f0·prev1 + f1·prev2) / 64, clamped to int16.
///   Filters 0-3:  f0 = {0, 60, 115, 98},  f1 = {0, 0, -52, -55}.
///   Mono plays units 0-7 in order; stereo routes even units to the left channel
///   and odd units to the right, each channel keeping its own two-sample history.
/// </summary>
public static class XaAdpcm
{
    public const int GroupsPerSector = 18;
    public const int GroupSize = 128;
    public const int DataAreaSize = GroupsPerSector * GroupSize;   // 2304
    private const int UnitsPerGroup = 8;
    private const int SamplesPerUnit = 28;

    private static readonly int[] FilterPos = { 0, 60, 115, 98 };
    private static readonly int[] FilterNeg = { 0, 0, -52, -55 };

    /// <summary>The two-sample history the decoder carries between groups (and
    /// between sectors of the same stream), per channel.</summary>
    public sealed class State
    {
        public int Prev1L, Prev2L, Prev1R, Prev2R;
    }

    /// <summary>Coding info from an XA subheader byte: sample rate and channels.</summary>
    public readonly record struct CodingInfo(int SampleRate, bool Stereo, bool EightBit)
    {
        public static CodingInfo Parse(byte codingByte) => new(
            SampleRate: ((codingByte >> 2) & 0x1) == 1 ? 18900 : 37800,
            Stereo: (codingByte & 0x3) == 1,
            EightBit: ((codingByte >> 4) & 0x1) == 1);
    }

    /// <summary>Decode one 2304-byte data area into interleaved 16-bit PCM
    /// (stereo) or mono samples, advancing <paramref name="state"/> so the next
    /// sector continues seamlessly. 4-bit mode only (the common case); 8-bit XA is
    /// rare and not decoded here.</summary>
    public static short[] DecodeDataArea(ReadOnlySpan<byte> data, bool stereo, State state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (data.Length < DataAreaSize)
            throw new ArgumentException($"XA data area is {DataAreaSize} bytes; got {data.Length}.", nameof(data));

        // Per group: 8 units × 28 samples. Stereo splits into 112 L + 112 R frames.
        var output = new List<short>(GroupsPerSector * UnitsPerGroup * SamplesPerUnit);

        // Scratch buffers reused across groups (hoisted out of the loop).
        Span<short> left = stackalloc short[UnitsPerGroup / 2 * SamplesPerUnit];   // 112
        Span<short> right = stackalloc short[UnitsPerGroup / 2 * SamplesPerUnit];
        Span<short> mono = stackalloc short[UnitsPerGroup * SamplesPerUnit];       // 224

        for (int g = 0; g < GroupsPerSector; g++)
        {
            var group = data.Slice(g * GroupSize, GroupSize);

            if (stereo)
            {
                int li = 0, ri = 0;
                for (int u = 0; u < UnitsPerGroup; u++)
                {
                    if ((u & 1) == 0) li = DecodeUnit(group, u, ref state.Prev1L, ref state.Prev2L, left, li);
                    else ri = DecodeUnit(group, u, ref state.Prev1R, ref state.Prev2R, right, ri);
                }
                for (int i = 0; i < SamplesPerUnit * (UnitsPerGroup / 2); i++)
                {
                    output.Add(left[i]);
                    output.Add(right[i]);
                }
            }
            else
            {
                int mi = 0;
                for (int u = 0; u < UnitsPerGroup; u++)
                    mi = DecodeUnit(group, u, ref state.Prev1L, ref state.Prev2L, mono, mi);
                for (int i = 0; i < mi; i++) output.Add(mono[i]);
            }
        }

        return output.ToArray();
    }

    /// <summary>Decode a sequence of 2304-byte data areas (one per XA sector) into
    /// one continuous PCM buffer.</summary>
    public static short[] DecodeSectors(IEnumerable<byte[]> dataAreas, bool stereo)
    {
        ArgumentNullException.ThrowIfNull(dataAreas);
        var state = new State();
        var all = new List<short>();
        foreach (var area in dataAreas)
            all.AddRange(DecodeDataArea(area, stereo, state));
        return all.ToArray();
    }

    // Decode one sound unit's 28 samples into `dest` starting at `at`; returns the
    // new write index. `prev1`/`prev2` are this channel's history.
    private static int DecodeUnit(ReadOnlySpan<byte> group, int unit,
                                  ref int prev1, ref int prev2, Span<short> dest, int at)
    {
        byte param = SoundParam(group, unit);
        int filter = Math.Min((param >> 4) & 0x0F, 3);
        int range = param & 0x0F;
        int shift = range <= 12 ? 12 - range : 0;   // ranges 13-15 are invalid → silence-ish
        int f0 = FilterPos[filter], f1 = FilterNeg[filter];

        for (int i = 0; i < SamplesPerUnit; i++)
        {
            byte b = group[16 + i * 4 + unit / 2];
            int nibble = (unit & 1) == 0 ? b & 0x0F : (b >> 4) & 0x0F;
            int s = SignExtend4(nibble) << shift;
            s += (prev1 * f0 + prev2 * f1) >> 6;
            short sample = (short)Math.Clamp(s, short.MinValue, short.MaxValue);
            dest[at++] = sample;
            prev2 = prev1;
            prev1 = sample;
        }
        return at;
    }

    // Sound-parameter byte for a unit: SP0-3 at header 0-3, SP4-7 at 8-11 (the
    // duplicated copies at 4-7 / 12-15 are identical).
    private static byte SoundParam(ReadOnlySpan<byte> group, int unit) =>
        unit < 4 ? group[unit] : group[unit + 4];

    private static int SignExtend4(int nibble) => (nibble << 28) >> 28;
}
