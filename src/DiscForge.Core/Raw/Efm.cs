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
/// Note: the byte→codeword <i>assignment</i> here is a canonical enumeration of the valid 14-bit words,
/// not the licensed ECMA-130 table; the run-length and DSV behaviour it models — which is what governs
/// readability — is faithful, and the real table drops in as a data swap if licensed.
/// </summary>
public static class Efm
{
    private const int MergeBits = 3;
    private const int WordBits = 14;

    private static readonly int[] ByteToCode = new int[256];
    private static readonly Dictionary<int, int> CodeToByte = new();

    static Efm()
    {
        var valid = new List<int>();
        for (int w = 0; w < 1 << WordBits && valid.Count < 256; w++)
            if (IsValidCodeword(w)) valid.Add(w);
        if (valid.Count < 256)
            throw new InvalidOperationException($"Only {valid.Count} valid EFM codewords enumerated (need 256).");
        for (int b = 0; b < 256; b++) { ByteToCode[b] = valid[b]; CodeToByte[valid[b]] = b; }
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
