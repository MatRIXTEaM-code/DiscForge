// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Raw;

namespace DiscForge.Core.Recovery;

/// <summary>
/// Pure, hardware-free consensus over N raw reads of the SAME sector range — the read-back half of
/// <see cref="AdaptiveReread"/>'s "Tier B". Each read is full raw 2448-byte sectors (2352 main +
/// 96-byte interleaved P-W sub-channel). Sub-channel Q reads on CD jitter: a drive can decode one
/// random sector's Q to the wrong address (or a wrong CRC) on any given pass, and a single-pass
/// read-back cannot tell that transient mis-read apart from a genuinely mis-addressed burn. Voting
/// over several reads settles it — the mis-read wanders, the truth is stable — which is exactly how
/// you distinguish read jitter from a disc defect rather than guessing.
///
/// The merge is deliberately conservative about what it will emit:
/// <list type="bullet">
/// <item><b>Main channel</b> — per-byte majority. A stable read is returned unchanged; a transient
/// dropout in one pass is out-voted by the others.</item>
/// <item><b>Sub-channel</b> — a WHOLE real 96-byte block is chosen, never a byte-blend. Blending
/// bytes across reads could synthesize a Q whose CRC is valid by accident but that no read ever
/// returned; so the merge only ever emits a block some drive actually produced. Among the reads
/// whose Q is CRC-valid, it picks the block whose decoded Q is shared by the most reads (lowest
/// pass index breaks a tie). That out-votes the wandering mis-read.</item>
/// <item><b>Protection is preserved, not "repaired"</b> — when NO read has a CRC-valid Q, the sector
/// is a deliberately-corrupt Q (LibCrypt and kin) that is stable on the disc, or media damage. The
/// merge emits the raw block the most reads agree on, verbatim, and never fabricates a valid Q.
/// Consensus removes read jitter; it must not remove what is really on the disc.</item>
/// </list>
///
/// It is a pure function of the read buffers, so it is proven in tests against synthetic flaky-Q
/// reads and drops onto real hardware unchanged (<c>RawDiscReader.ReadConsensus</c> supplies the
/// reads). Read-side only: it defeats nothing and re-encodes nothing.
/// </summary>
public static class SubchannelConsensus
{
    public const int MainBytes = 2352;
    public const int SubBytes = 96;
    public const int SectorBytes = MainBytes + SubBytes;   // 2448

    public sealed record Report
    {
        public required int Passes { get; init; }
        public required long Sectors { get; init; }
        /// <summary>Sectors whose emitted sub-channel differs from a single (first) read — a
        /// minority or CRC-invalid Q that was out-voted.</summary>
        public required long SubCorrected { get; init; }
        /// <summary>Sectors whose emitted main channel differs from the first read — a transient
        /// dropout repaired by majority.</summary>
        public required long MainCorrected { get; init; }
        /// <summary>Sectors where no read produced a CRC-valid Q; the majority raw block was
        /// preserved verbatim (possible sub-channel protection or media damage — never "fixed").</summary>
        public required long PreservedNoValidQ { get; init; }

        public string Summary =>
            $"Consensus over {Passes} reads across {Sectors:N0} sector(s): " +
            $"{SubCorrected:N0} sub-channel Q out-voted, {MainCorrected:N0} main-channel dropout(s) " +
            $"repaired, {PreservedNoValidQ:N0} preserved verbatim (no CRC-valid Q in any read).";
    }

    /// <summary>
    /// Merge <paramref name="passes"/> (each a raw dump of the same <paramref name="sectors"/>
    /// sectors, ≥ <c>sectors*2448</c> bytes) into <paramref name="output"/> by the voting rule above.
    /// </summary>
    public static Report Merge(IReadOnlyList<byte[]> passes, long sectors, Stream output)
    {
        ArgumentNullException.ThrowIfNull(passes);
        ArgumentNullException.ThrowIfNull(output);
        int n = passes.Count;
        if (n < 2)
            throw new ArgumentException("Consensus needs at least two reads.", nameof(passes));
        if (sectors <= 0)
            throw new ArgumentException("Consensus needs a positive sector count.", nameof(sectors));
        long need = sectors * SectorBytes;
        for (int i = 0; i < n; i++)
            if (passes[i] is null || passes[i].Length < need)
                throw new ArgumentException(
                    $"Read {i} is shorter than {sectors:N0} sectors ({need:N0} bytes).", nameof(passes));

        var outSec = new byte[SectorBytes];
        var counts = new int[256];
        long subCorrected = 0, mainCorrected = 0, preserved = 0;

        for (long s = 0; s < sectors; s++)
        {
            int baseOff = checked((int)(s * SectorBytes));

            // ---- main channel: per-byte majority (fast path when all reads agree) ----
            bool mainChanged = false;
            for (int b = 0; b < MainBytes; b++)
            {
                int off = baseOff + b;
                byte v0 = passes[0][off];
                bool allSame = true;
                for (int i = 1; i < n; i++)
                    if (passes[i][off] != v0) { allSame = false; break; }
                if (allSame) { outSec[b] = v0; continue; }

                Array.Clear(counts, 0, 256);
                for (int i = 0; i < n; i++) counts[passes[i][off]]++;
                int best = v0, bestC = counts[v0];
                for (int i = 0; i < n; i++)
                {
                    byte val = passes[i][off];
                    if (counts[val] > bestC) { bestC = counts[val]; best = val; }
                }
                outSec[b] = (byte)best;
                if (best != v0) mainChanged = true;
            }
            if (mainChanged) mainCorrected++;

            // ---- sub channel: choose a whole real 96-byte block by CRC-valid-Q majority ----
            int subOff = baseOff + MainBytes;
            int chosen = ChooseSubPass(passes, subOff, n, out bool anyValidQ);
            passes[chosen].AsSpan(subOff, SubBytes).CopyTo(outSec.AsSpan(MainBytes, SubBytes));

            if (!passes[chosen].AsSpan(subOff, SubBytes)
                    .SequenceEqual(passes[0].AsSpan(subOff, SubBytes)))
                subCorrected++;
            if (!anyValidQ) preserved++;

            output.Write(outSec, 0, SectorBytes);
        }

        return new Report
        {
            Passes = n,
            Sectors = sectors,
            SubCorrected = subCorrected,
            MainCorrected = mainCorrected,
            PreservedNoValidQ = preserved,
        };
    }

    /// <summary>
    /// Index of the read whose 96-byte sub-channel block should be emitted for this sector. Among
    /// reads with a CRC-valid Q, the one whose decoded Q is shared by the most reads (lowest pass
    /// index wins a tie). If no read has a CRC-valid Q, the raw block the most reads agree on.
    /// </summary>
    private static int ChooseSubPass(IReadOnlyList<byte[]> passes, int subOff, int n, out bool anyValidQ)
    {
        var q = new byte[n][];
        var valid = new bool[n];
        anyValidQ = false;
        var qi = new byte[12];
        for (int i = 0; i < n; i++)
        {
            RawSubchannel.ExtractQ(passes[i].AsSpan(subOff, SubBytes), qi);
            q[i] = (byte[])qi.Clone();
            valid[i] = RawSubchannel.QCrcValid(qi);
            if (valid[i]) anyValidQ = true;
        }

        if (anyValidQ)
        {
            int bestPass = 0, bestCount = -1;
            for (int i = 0; i < n; i++)
            {
                if (!valid[i]) continue;
                int c = 0;
                for (int j = 0; j < n; j++)
                    if (valid[j] && q[j].AsSpan().SequenceEqual(q[i])) c++;
                if (c > bestCount) { bestCount = c; bestPass = i; }
            }
            return bestPass;
        }

        // No CRC-valid Q anywhere — preserve the raw block the most reads agree on, verbatim.
        int bp = 0, bc = -1;
        for (int i = 0; i < n; i++)
        {
            var si = passes[i].AsSpan(subOff, SubBytes);
            int c = 0;
            for (int j = 0; j < n; j++)
                if (passes[j].AsSpan(subOff, SubBytes).SequenceEqual(si)) c++;
            if (c > bc) { bc = c; bp = i; }
        }
        return bp;
    }
}
