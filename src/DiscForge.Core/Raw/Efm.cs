// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Raw;

/// <summary>The channel-level shape of an EFM-encoded stream — the numbers that decide whether a
/// laser can track it.</summary>
public sealed record EfmChannel
{
    public required int ChannelBits { get; init; }
    /// <summary>Peak absolute Digital Sum Value — how far the running DC balance wandered. The whole
    /// point of EFM + merging bits is to keep this small; a large excursion is a stressed stream.</summary>
    public required int MaxAbsDsv { get; init; }
    /// <summary>Final DSV at the end of the stream.</summary>
    public required int EndDsv { get; init; }
    /// <summary>Shortest and longest pit/land run, in channel-bit (T) units. The (d,k) constraint is 3..11.</summary>
    public required int MinRunT { get; init; }
    public required int MaxRunT { get; init; }
    public required double MeanRunT { get; init; }
    /// <summary>Fraction of channel bits that are transitions — low means long runs, weak tracking.</summary>
    public required double TransitionDensity { get; init; }
    /// <summary>Every run length fell inside the legal 3T..11T window.</summary>
    public required bool ConstraintOk { get; init; }
}

/// <summary>
/// Eight-to-Fourteen Modulation — the physical channel code beneath a CD, a layer almost no
/// preservation tool touches. Each byte becomes a 14-bit channel word chosen to obey the run-length
/// rule (between 2 and 10 zeros between transitions, i.e. pit/land lengths of 3T..11T), and 3 merging
/// bits between words are picked to hold that rule across the boundary and to keep the Digital Sum
/// Value — the running DC balance the laser's servo depends on — near zero. This implements that
/// machinery: encode/decode with DSV-minimising merging, and a channel analysis (DSV excursion, run-
/// length spectrum, transition density) that is the substrate for weak-sector prediction.
///
/// The byte↔codeword table below IS the authoritative ECMA-130 Annex D assignment (the "data swap"
/// this project's flux/RF moonshot was waiting on — see docs/DIFFERENTIATORS.md and
/// <see cref="FluxDemodulator"/>/<see cref="FluxDecoder"/>). It is transcribed from the public-domain-
/// equivalent, GPL-licensed EFM dictionary in Sidney Cadot's <c>laser2wav</c> project (used by
/// happycube's <c>cd-decode</c>), itself derived from the published ECMA-130 standard — the same table
/// every CD drive, burner and RF-decode tool implements, since it is the one physical encoding every
/// audio and data CD ever pressed actually uses. GPL-3.0-or-later throughout, so it drops in here
/// cleanly. Two further 14-bit patterns from the same standard — <see cref="Sync0"/> and
/// <see cref="Sync1"/> — mark the first two frames of each 98-frame sector; they are not yet wired
/// into decoding (frame synchronisation is the next stage, not this one) but are recorded here as the
/// same authoritative constants, so that stage has nowhere else to get them wrong from.
/// </summary>
public static class Efm
{
    private const int MergeBits = 3;
    private const int WordBits = 14;

    /// <summary>The special 14-bit CONTROL pattern marking the first frame of a 98-frame sector
    /// (ECMA-130). Not a data codeword — never appears in <see cref="ByteToCode"/> or
    /// <see cref="CodeToByte"/>. Reserved for the frame-synchronisation stage.</summary>
    public const int Sync0 = 0x0801;

    /// <summary>The special 14-bit CONTROL pattern marking the second frame of a 98-frame sector
    /// (ECMA-130), immediately following <see cref="Sync0"/>. Reserved for the frame-synchronisation
    /// stage, same as <see cref="Sync0"/>.</summary>
    public const int Sync1 = 0x0012;

    // The authoritative ECMA-130 byte→codeword table, byte index 0..255. See the class doc comment
    // for provenance. Verified (see the static constructor below) to contain 256 distinct 14-bit
    // codewords, none equal to Sync0/Sync1, each individually satisfying EFM's own run-length rule.
    private static readonly int[] Table =
    {
        0x1220, 0x2100, 0x2420, 0x2220, 0x1100, 0x0110, 0x0420, 0x0900, // 0-7
        0x1240, 0x2040, 0x2440, 0x2240, 0x1040, 0x0040, 0x0440, 0x0840, // 8-15
        0x2020, 0x2080, 0x2480, 0x0820, 0x1080, 0x0080, 0x0480, 0x0880, // 16-23
        0x1210, 0x2010, 0x2410, 0x2210, 0x1010, 0x0210, 0x0410, 0x0810, // 24-31
        0x0020, 0x2108, 0x0220, 0x0920, 0x1108, 0x0108, 0x1020, 0x0908, // 32-39
        0x1248, 0x2048, 0x2448, 0x2248, 0x1048, 0x0048, 0x0448, 0x0848, // 40-47
        0x0100, 0x2088, 0x2488, 0x2110, 0x1088, 0x0088, 0x0488, 0x0888, // 48-55
        0x1208, 0x2008, 0x2408, 0x2208, 0x1008, 0x0208, 0x0408, 0x0808, // 56-63
        0x1224, 0x2124, 0x2424, 0x2224, 0x1124, 0x0024, 0x0424, 0x0924, // 64-71
        0x1244, 0x2044, 0x2444, 0x2244, 0x1044, 0x0044, 0x0444, 0x0844, // 72-79
        0x2024, 0x2084, 0x2484, 0x0824, 0x1084, 0x0084, 0x0484, 0x0884, // 80-87
        0x1204, 0x2004, 0x2404, 0x2204, 0x1004, 0x0204, 0x0404, 0x0804, // 88-95
        0x1222, 0x2122, 0x2422, 0x2222, 0x1122, 0x0022, 0x1024, 0x0922, // 96-103
        0x1242, 0x2042, 0x2442, 0x2242, 0x1042, 0x0042, 0x0442, 0x0842, // 104-111
        0x2022, 0x2082, 0x2482, 0x0822, 0x1082, 0x0082, 0x0482, 0x0882, // 112-119
        0x1202, 0x0248, 0x2402, 0x2202, 0x1002, 0x0202, 0x0402, 0x0802, // 120-127
        0x1221, 0x2121, 0x2421, 0x2221, 0x1121, 0x0021, 0x0421, 0x0921, // 128-135
        0x1241, 0x2041, 0x2441, 0x2241, 0x1041, 0x0041, 0x0441, 0x0841, // 136-143
        0x2021, 0x2081, 0x2481, 0x0821, 0x1081, 0x0081, 0x0481, 0x0881, // 144-151
        0x1201, 0x2090, 0x2401, 0x2201, 0x1090, 0x0201, 0x0401, 0x0890, // 152-159
        0x0221, 0x2109, 0x1110, 0x0121, 0x1109, 0x0109, 0x1021, 0x0909, // 160-167
        0x1249, 0x2049, 0x2449, 0x2249, 0x1049, 0x0049, 0x0449, 0x0849, // 168-175
        0x0120, 0x2089, 0x2489, 0x0910, 0x1089, 0x0089, 0x0489, 0x0889, // 176-183
        0x1209, 0x2009, 0x2409, 0x2209, 0x1009, 0x0209, 0x0409, 0x0809, // 184-191
        0x1120, 0x2111, 0x2490, 0x0224, 0x1111, 0x0111, 0x0490, 0x0911, // 192-199
        0x0241, 0x2101, 0x0244, 0x0240, 0x1101, 0x0101, 0x0090, 0x0901, // 200-207
        0x0124, 0x2091, 0x2491, 0x2120, 0x1091, 0x0091, 0x0491, 0x0891, // 208-215
        0x1211, 0x2011, 0x2411, 0x2211, 0x1011, 0x0211, 0x0411, 0x0811, // 216-223
        0x1102, 0x0102, 0x2112, 0x0902, 0x1112, 0x0112, 0x1022, 0x0912, // 224-231
        0x2102, 0x2104, 0x0249, 0x0242, 0x1104, 0x0104, 0x0422, 0x0904, // 232-239
        0x0122, 0x2092, 0x2492, 0x0222, 0x1092, 0x0092, 0x0492, 0x0892, // 240-247
        0x1212, 0x2012, 0x2412, 0x2212, 0x1012, 0x0212, 0x0412, 0x0812, // 248-255
    };

    private static readonly int[] ByteToCode = new int[256];
    private static readonly Dictionary<int, int> CodeToByte = new();

    static Efm()
    {
        if (Table.Length != 256)
            throw new InvalidOperationException($"EFM table has {Table.Length} entries (need 256).");
        for (int b = 0; b < 256; b++)
        {
            int w = Table[b];
            if (w == Sync0 || w == Sync1)
                throw new InvalidOperationException($"EFM table entry for byte {b} collides with a sync pattern.");
            if (!IsValidCodeword(w))
                throw new InvalidOperationException(
                    $"EFM table entry for byte {b} (0x{w:X4}) violates the run-length rule — check the transcription.");
            if (!CodeToByte.TryAdd(w, b))
                throw new InvalidOperationException(
                    $"EFM table entry for byte {b} (0x{w:X4}) duplicates byte {CodeToByte[w]}'s codeword.");
            ByteToCode[b] = w;
        }
    }

    public static int CodebookSize => CodeToByte.Count;

    /// <summary>Encode bytes to the EFM channel bit stream (word + 3 merging bits per byte).</summary>
    public static bool[] Encode(ReadOnlySpan<byte> data)
    {
        var bits = new List<bool>(data.Length * (WordBits + MergeBits));
        int level = +1, dsv = 0;
        int prevTrailing = -1;   // trailing zeros of the previously-emitted word

        foreach (byte b in data)
        {
            var word = WordBitsOf(ByteToCode[b]);
            int lead = LeadingZeros(word), trail = TrailingZeros(word);

            if (prevTrailing >= 0)
            {
                bool[] merge = ChooseMerge(prevTrailing, lead, level, dsv);
                AppendAndAccumulate(bits, merge, ref level, ref dsv);
            }
            AppendAndAccumulate(bits, word, ref level, ref dsv);
            prevTrailing = trail;
        }
        return bits.ToArray();
    }

    /// <summary>Decode a channel bit stream back to <paramref name="byteCount"/> bytes.</summary>
    public static byte[] Decode(ReadOnlySpan<bool> channel, int byteCount)
    {
        var outp = new byte[byteCount];
        int pos = 0;
        for (int i = 0; i < byteCount; i++)
        {
            if (i > 0) pos += MergeBits;                 // skip the merging bits
            int code = 0;
            for (int k = 0; k < WordBits; k++)
                code = (code << 1) | (channel[pos + k] ? 1 : 0);
            if (!CodeToByte.TryGetValue(code, out int val))
                throw new InvalidDataException($"Invalid EFM codeword at byte {i}.");
            outp[i] = (byte)val;
            pos += WordBits;
        }
        return outp;
    }

    /// <summary>Encode and measure the channel stream's physical health.</summary>
    public static EfmChannel Analyze(ReadOnlySpan<byte> data)
    {
        var bits = Encode(data);
        int level = +1, dsv = 0, maxAbs = 0;
        int run = 0, minRun = int.MaxValue, maxRun = 0, transitions = 0;
        long runSum = 0; int runCount = 0;
        bool seenTransition = false;

        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i])   // a transition (NRZI): toggle physical level
            {
                // Only complete runs BETWEEN two transitions are constrained; the leading run before
                // the first transition (and trailing run after the last) are frame-edge effects.
                if (seenTransition)
                {
                    int t = run + 1;                     // run of `run` zeros = (run+1)T
                    minRun = Math.Min(minRun, t); maxRun = Math.Max(maxRun, t);
                    runSum += t; runCount++;
                }
                level = -level;
                transitions++;
                seenTransition = true;
                run = 0;
            }
            else run++;
            dsv += level;
            maxAbs = Math.Max(maxAbs, Math.Abs(dsv));
        }

        bool ok = runCount == 0 || (minRun >= 3 && maxRun <= 11);
        return new EfmChannel
        {
            ChannelBits = bits.Length,
            MaxAbsDsv = maxAbs,
            EndDsv = dsv,
            MinRunT = runCount == 0 ? 0 : minRun,
            MaxRunT = maxRun,
            MeanRunT = runCount == 0 ? 0 : (double)runSum / runCount,
            TransitionDensity = bits.Length == 0 ? 0 : transitions / (double)bits.Length,
            ConstraintOk = ok,
        };
    }

    // ---- internals ----------------------------------------------------------

    // A codeword is valid when, taken alone, every run of zeros between 1s is in [2,10] and its
    // leading/trailing zero runs are in [2,9] — so 3 merging bits can always bridge two of them
    // while keeping the boundary runs legal.
    private static bool IsValidCodeword(int w)
    {
        int firstOne = -1, lastOne = -1, prevOne = -1;
        for (int i = 0; i < WordBits; i++)
        {
            if ((w & (1 << (WordBits - 1 - i))) == 0) continue;
            if (firstOne < 0) firstOne = i;
            if (prevOne >= 0)
            {
                int gap = i - prevOne - 1;
                if (gap < 2 || gap > 10) return false;
            }
            prevOne = i;
            lastOne = i;
        }
        if (firstOne < 0) return false;                  // must carry at least one transition
        int lead = firstOne, trail = WordBits - 1 - lastOne;
        // Leading/trailing zeros in [0,8] guarantee that some 3-bit merge always bridges two words
        // while holding the run-length rule at the boundary.
        return lead <= 8 && trail <= 8;
    }

    // Choose the 3 merging bits: among those that keep every boundary run legal, take the one that
    // holds the DSV closest to zero. At least one is always legal for codewords with trailing/leading
    // zeros in [0,8].
    private static bool[] ChooseMerge(int tz, int lz, int level, int dsv)
    {
        bool[] best = null!;
        int bestScore = int.MaxValue;
        for (int code = 0; code < 8; code++)
        {
            var m = new[] { (code & 4) != 0, (code & 2) != 0, (code & 1) != 0 };
            if (!BoundaryLegal(tz, m, lz)) continue;

            int lv = level, d = dsv, peak = 0;
            foreach (var bit in m) { if (bit) lv = -lv; d += lv; peak = Math.Max(peak, Math.Abs(d)); }
            if (peak < bestScore) { bestScore = peak; best = m; }
        }
        // Guaranteed non-null by construction; fall back defensively to 010.
        return best ?? new[] { false, true, false };
    }

    // The boundary spans: [prev's last 1] tz zeros | merge | lz zeros [next's first 1].
    // Legal iff every run of zeros between consecutive 1s there is in [2,10].
    private static bool BoundaryLegal(int tz, bool[] merge, int lz)
    {
        var seq = new List<bool> { true };
        for (int i = 0; i < tz; i++) seq.Add(false);
        seq.AddRange(merge);
        for (int i = 0; i < lz; i++) seq.Add(false);
        seq.Add(true);

        int prev = -1;
        for (int i = 0; i < seq.Count; i++)
        {
            if (!seq[i]) continue;
            if (prev >= 0) { int gap = i - prev - 1; if (gap < 2 || gap > 10) return false; }
            prev = i;
        }
        return true;
    }

    private static void AppendAndAccumulate(List<bool> bits, bool[] add, ref int level, ref int dsv)
    {
        foreach (var bit in add)
        {
            if (bit) level = -level;
            dsv += level;
            bits.Add(bit);
        }
    }

    private static bool[] WordBitsOf(int code)
    {
        var w = new bool[WordBits];
        for (int i = 0; i < WordBits; i++) w[i] = (code & (1 << (WordBits - 1 - i))) != 0;
        return w;
    }

    private static int LeadingZeros(bool[] w)
    {
        int i = 0; while (i < w.Length && !w[i]) i++; return i;
    }

    private static int TrailingZeros(bool[] w)
    {
        int i = 0; while (i < w.Length && !w[w.Length - 1 - i]) i++; return i;
    }
}
