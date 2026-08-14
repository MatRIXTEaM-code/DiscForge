// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Raw;

/// <summary>
/// CD-ROM sector error detection and correction codes (ECMA-130 §14):
/// the 32-bit EDC and the Reed-Solomon Product Code (RSPC) P/Q parity that
/// turn 2048 bytes of user data into a valid raw Mode 1 sector.
///
/// Needed because a RAW burn bypasses the drive's encoder entirely: if the
/// source image stores cooked 2048-byte sectors (an ISO, or a CDI written in
/// 2048 mode), DiscForge must compute everything the drive would have —
/// sync, header, EDC, and both ECC fields — or the disc is unreadable.
///
/// Layout of a raw Mode 1 sector (byte offsets):
///   0..11    sync (00 FF×10 00)
///   12..15   header (MSF in BCD + mode)
///   16..2063 user data (2048)
///   2064..2067 EDC over bytes 0..2063, little-endian
///   2068..2075 zero (intermediate field)
///   2076..2247 ECC P parity (172 bytes)
///   2248..2351 ECC Q parity (104 bytes)
///
/// The RSPC treats bytes 12..2075 as 1032 little-endian words arranged as
/// 24 rows × 43 columns, split into an LSB plane and an MSB plane, each
/// encoded over GF(2^8) with polynomial 0x11D:
///   P: 43 word-columns × 2 planes = 86 codewords of 24 bytes (word stride
///      43), RS(26,24) — parity at word offsets 1032+col and 1075+col.
///   Q: 26 word-diagonals × 2 planes = 52 codewords of 43 bytes over the
///      1118-word region including P (word (43·d + 44·j) mod 1118),
///      RS(45,43) — parity at word offsets 1118+d and 1144+d.
/// Each RS code's generator is (x + α^0)(x + α^1); the tests verify the
/// encoder by evaluating every codeword at both roots and requiring zero
/// syndromes, which is an independent algebraic check rather than a
/// re-run of the encoder.
/// </summary>
public static class EdcEcc
{
    // ---- EDC ---------------------------------------------------------------

    // EDC polynomial (ECMA-130 14.3), bit-reversed implementation constant.
    private static readonly uint[] EdcTable = BuildEdcTable();

    private static uint[] BuildEdcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint edc = i;
            for (int b = 0; b < 8; b++)
                edc = (edc >> 1) ^ ((edc & 1) != 0 ? 0xD8018001u : 0);
            t[i] = edc;
        }
        return t;
    }

    /// <summary>EDC over the given bytes (for Mode 1: sector bytes 0..2063).</summary>
    public static uint ComputeEdc(ReadOnlySpan<byte> data) => ComputeEdc(data, 0);

    /// <summary>
    /// EDC continued from a running <paramref name="seed"/>, so a checksum over a
    /// whole file can be accumulated across chunks: the result of one call feeds the
    /// seed of the next, and the final value equals the EDC over the concatenation.
    /// (The CD sector EDC is a plain LSB-first CRC, so it composes this way.)
    /// </summary>
    public static uint ComputeEdc(ReadOnlySpan<byte> data, uint seed)
    {
        uint edc = seed;
        foreach (byte b in data)
            edc = (edc >> 8) ^ EdcTable[(edc ^ b) & 0xFF];
        return edc;
    }

    // ---- GF(2^8) -----------------------------------------------------------

    private static readonly byte[] GfExp = new byte[512];
    private static readonly byte[] GfLog = new byte[256];

    static EdcEcc()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            GfExp[i] = (byte)x;
            GfLog[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11D;
        }
        for (int i = 255; i < 512; i++) GfExp[i] = GfExp[i - 255];
    }

    internal static byte GfMul(byte a, byte b)
        => a == 0 || b == 0 ? (byte)0 : GfExp[GfLog[a] + GfLog[b]];

    /// <summary>α^power — exposed for the syndrome checks in the tests.</summary>
    internal static byte GfPow(int power) => GfExp[((power % 255) + 255) % 255];

    /// <summary>
    /// Verify an UNSCRAMBLED raw Mode 1 sector: recompute the EDC and evaluate
    /// every RSPC codeword's syndromes at both generator roots. This is the
    /// read-side counterpart of <see cref="FillMode1"/> and deliberately does
    /// not share its encoding loop — verification by independent evaluation is
    /// what makes it evidence. Order-agnostic where it matters: a sector from
    /// a real disc that passes here proves the parity conventions match.
    /// </summary>
    public static (bool EdcOk, bool EccOk) VerifyMode1(ReadOnlySpan<byte> sector)
    {
        if (sector.Length != 2352)
            throw new ArgumentException("A raw sector is 2352 bytes.", nameof(sector));

        uint edc = ComputeEdc(sector[..2064]);
        uint stored = (uint)sector[2064] | ((uint)sector[2065] << 8)
                    | ((uint)sector[2066] << 16) | ((uint)sector[2067] << 24);
        bool edcOk = edc == stored;

        bool eccOk = true;
        Span<byte> cw = stackalloc byte[45];
        for (int plane = 0; plane < 2 && eccOk; plane++)
        {
            for (int col = 0; col < 43 && eccOk; col++)
            {
                for (int row = 0; row < 24; row++)
                    cw[row] = sector[12 + 2 * (col + 43 * row) + plane];
                cw[24] = sector[12 + 2 * (1032 + col) + plane];
                cw[25] = sector[12 + 2 * (1075 + col) + plane];
                eccOk &= SyndromesZero(cw[..26]);
            }
            for (int diag = 0; diag < 26 && eccOk; diag++)
            {
                for (int j = 0; j < 43; j++)
                    cw[j] = sector[12 + 2 * ((43 * diag + 44 * j) % 1118) + plane];
                cw[43] = sector[12 + 2 * (1118 + diag) + plane];
                cw[44] = sector[12 + 2 * (1144 + diag) + plane];
                eccOk &= SyndromesZero(cw[..45]);
            }
        }
        return (edcOk, eccOk);
    }

    /// <summary>
    /// Verify a raw Mode 2 Form 1 sector: recompute the EDC (over 16..2071) and
    /// evaluate every RSPC codeword's syndromes with the header treated as zero,
    /// exactly as <see cref="FillMode2Form1"/> encodes it. Independent of the
    /// encoder — it evaluates, it does not re-run the fill.
    /// </summary>
    public static (bool EdcOk, bool EccOk) VerifyMode2Form1(ReadOnlySpan<byte> sector)
    {
        if (sector.Length != 2352)
            throw new ArgumentException("A raw sector is 2352 bytes.", nameof(sector));

        uint edc = ComputeEdc(sector.Slice(16, 2056));
        uint stored = (uint)sector[2072] | ((uint)sector[2073] << 8)
                    | ((uint)sector[2074] << 16) | ((uint)sector[2075] << 24);
        bool edcOk = edc == stored;

        // Copy so the header can be zeroed for the parity evaluation.
        Span<byte> s = stackalloc byte[2352];
        sector.CopyTo(s);
        s.Slice(12, 4).Clear();

        bool eccOk = true;
        Span<byte> cw = stackalloc byte[45];
        for (int plane = 0; plane < 2 && eccOk; plane++)
        {
            for (int col = 0; col < 43 && eccOk; col++)
            {
                for (int row = 0; row < 24; row++)
                    cw[row] = s[12 + 2 * (col + 43 * row) + plane];
                cw[24] = s[12 + 2 * (1032 + col) + plane];
                cw[25] = s[12 + 2 * (1075 + col) + plane];
                eccOk &= SyndromesZero(cw[..26]);
            }
            for (int diag = 0; diag < 26 && eccOk; diag++)
            {
                for (int j = 0; j < 43; j++)
                    cw[j] = s[12 + 2 * ((43 * diag + 44 * j) % 1118) + plane];
                cw[43] = s[12 + 2 * (1118 + diag) + plane];
                cw[44] = s[12 + 2 * (1144 + diag) + plane];
                eccOk &= SyndromesZero(cw[..45]);
            }
        }
        return (edcOk, eccOk);
    }

    private static bool SyndromesZero(ReadOnlySpan<byte> codeword)
    {
        for (int root = 0; root <= 1; root++)
        {
            byte s = 0;
            byte a = GfPow(root);
            foreach (byte c in codeword) s = (byte)(GfMul(s, a) ^ c);
            if (s != 0) return false;
        }
        return true;
    }

    // ---- RSPC parity -------------------------------------------------------

    /// <summary>
    /// Compute the two parity bytes for one RS codeword whose generator is
    /// (x + 1)(x + α) = x² + gx + h with g = α+1 = 3, h = α = 2 — plain
    /// polynomial long division, one step per data byte.
    /// </summary>
    private static (byte p0, byte p1) RsParity(ReadOnlySpan<byte> data)
    {
        byte r0 = 0, r1 = 0;                       // remainder registers
        foreach (byte d in data)
        {
            byte f = (byte)(d ^ r0);
            r0 = (byte)(r1 ^ GfMul(f, 3));         // g = α + 1
            r1 = GfMul(f, 2);                      // h = α
        }
        return (r0, r1);
    }

    /// <summary>
    /// Fill EDC + intermediate + P + Q parity of a raw Mode 1 sector whose
    /// sync, header, and user data (bytes 0..2063) are already in place.
    /// </summary>
    public static void FillMode1(Span<byte> sector)
    {
        if (sector.Length != 2352)
            throw new ArgumentException("A raw sector is 2352 bytes.", nameof(sector));

        // EDC over 0..2063, stored LE at 2064.
        uint edc = ComputeEdc(sector[..2064]);
        sector[2064] = (byte)edc;
        sector[2065] = (byte)(edc >> 8);
        sector[2066] = (byte)(edc >> 16);
        sector[2067] = (byte)(edc >> 24);
        sector.Slice(2068, 8).Clear();             // intermediate field

        // The RSPC region: bytes 12..2075 as words; parity regions follow it.
        // Work per byte-plane (plane 0 = LSBs = even offsets from 12).
        Span<byte> cw = stackalloc byte[43];

        for (int plane = 0; plane < 2; plane++)
        {
            // P: 43 word-columns × 24 rows.
            for (int col = 0; col < 43; col++)
            {
                Span<byte> d = cw[..24];
                for (int row = 0; row < 24; row++)
                    d[row] = sector[12 + 2 * (col + 43 * row) + plane];
                var (p0, p1) = RsParity(d);
                sector[12 + 2 * (1032 + col) + plane] = p0;
                sector[12 + 2 * (1075 + col) + plane] = p1;
            }

            // Q: 26 word-diagonals × 43 over the 1118-word region (P included).
            for (int diag = 0; diag < 26; diag++)
            {
                Span<byte> d = cw[..43];
                for (int j = 0; j < 43; j++)
                    d[j] = sector[12 + 2 * ((43 * diag + 44 * j) % 1118) + plane];
                var (q0, q1) = RsParity(d);
                sector[12 + 2 * (1118 + diag) + plane] = q0;
                sector[12 + 2 * (1144 + diag) + plane] = q1;
            }
        }
    }

    /// <summary>
    /// Fill EDC + P + Q of a raw Mode 2 Form 1 sector — the form a PlayStation
    /// (or any CD-XA) data disc stores its filesystem in. Assumes the sync
    /// (0..11), header (12..15), 8-byte subheader (16..23) and 2048 user bytes
    /// (24..2071) are already in place.
    ///
    /// Two things differ from Mode 1. The EDC covers the subheader and user data
    /// (bytes 16..2071) and lands at 2072, not 2064. And the P/Q parity is
    /// computed with the 4-byte header treated as zero — CD-XA excludes the
    /// address from the ECC so the same file reads identically wherever it sits —
    /// so the header is blanked for the parity pass and restored afterwards.
    /// </summary>
    public static void FillMode2Form1(Span<byte> sector)
    {
        if (sector.Length != 2352)
            throw new ArgumentException("A raw sector is 2352 bytes.", nameof(sector));

        // EDC over the subheader + user data (16..2071), stored LE at 2072.
        uint edc = ComputeEdc(sector.Slice(16, 2056));
        sector[2072] = (byte)edc;
        sector[2073] = (byte)(edc >> 8);
        sector[2074] = (byte)(edc >> 16);
        sector[2075] = (byte)(edc >> 24);

        // ECC with the header address zeroed, then restored.
        Span<byte> savedHeader = stackalloc byte[4];
        sector.Slice(12, 4).CopyTo(savedHeader);
        sector.Slice(12, 4).Clear();

        Span<byte> cw = stackalloc byte[43];
        for (int plane = 0; plane < 2; plane++)
        {
            for (int col = 0; col < 43; col++)
            {
                Span<byte> d = cw[..24];
                for (int row = 0; row < 24; row++)
                    d[row] = sector[12 + 2 * (col + 43 * row) + plane];
                var (p0, p1) = RsParity(d);
                sector[12 + 2 * (1032 + col) + plane] = p0;
                sector[12 + 2 * (1075 + col) + plane] = p1;
            }
            for (int diag = 0; diag < 26; diag++)
            {
                Span<byte> d = cw[..43];
                for (int j = 0; j < 43; j++)
                    d[j] = sector[12 + 2 * ((43 * diag + 44 * j) % 1118) + plane];
                var (q0, q1) = RsParity(d);
                sector[12 + 2 * (1118 + diag) + plane] = q0;
                sector[12 + 2 * (1144 + diag) + plane] = q1;
            }
        }

        savedHeader.CopyTo(sector.Slice(12, 4));
    }

    /// <summary>
    /// Fill the EDC of a raw Mode 2 Form 2 sector. Form 2 carries no ECC at all:
    /// its only redundancy is a single EDC over the 8-byte subheader plus the 2324
    /// user bytes (sector bytes 16..2347), stored little-endian at 2348. Assumes the
    /// sync (0..11), header (12..15), subheader (16..23) and 2324 user bytes
    /// (24..2347) are already in place.
    ///
    /// The Form 2 EDC is defined as optional in the Yellow Book — some pressings
    /// leave those four bytes zero. Callers that need to reproduce such a sector
    /// byte-for-byte must detect the zero-EDC case themselves (e.g. an ECM encoder
    /// falls back to a literal when the stored EDC doesn't match this computation).
    /// </summary>
    public static void FillMode2Form2(Span<byte> sector)
    {
        if (sector.Length != 2352)
            throw new ArgumentException("A raw sector is 2352 bytes.", nameof(sector));

        // EDC over the subheader + user data (16..2347 = 2332 bytes), stored LE at 2348.
        uint edc = ComputeEdc(sector.Slice(16, 2332));
        sector[2348] = (byte)edc;
        sector[2349] = (byte)(edc >> 8);
        sector[2350] = (byte)(edc >> 16);
        sector[2351] = (byte)(edc >> 24);
    }
}
