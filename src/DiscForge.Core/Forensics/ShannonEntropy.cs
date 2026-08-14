// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Forensics;

/// <summary>
/// Shannon entropy of a byte stream — bits of information per byte, 0..8. Aaru exposes this as its
/// <c>entropy</c> verb, and it is a cheap, telling preservation measure: a value near 8 means the
/// data is already compressed, encrypted, or random (so it won't shrink and any "compression" of it
/// is suspicious), while a low value means padding, blanking, or structured/repetitive content. It
/// distinguishes a genuinely full disc from one padded with junk, and flags a region that is not
/// what its container claims. Pure and streaming (fixed memory regardless of image size), unit-tested.
/// </summary>
public static class ShannonEntropy
{
    public sealed record Result(long Bytes, double BitsPerByte)
    {
        /// <summary>0..1 — bits/byte over the 8-bit maximum. ~1.0 = incompressible/random.</summary>
        public double Ratio => BitsPerByte / 8.0;

        public string Character => Bytes == 0 ? "empty"
            : BitsPerByte < 0.5 ? "near-constant (padding / blanked)"
            : BitsPerByte < 4.0 ? "low (structured / repetitive)"
            : BitsPerByte < 7.5 ? "mixed"
            : BitsPerByte < 7.99 ? "high (compressed / media)"
            : "maximal (random / encrypted / already-compressed)";
    }

    /// <summary>Entropy of an in-memory buffer.</summary>
    public static Result Compute(ReadOnlySpan<byte> data)
    {
        Span<long> counts = stackalloc long[256];
        foreach (byte b in data) counts[b]++;
        return FromCounts(counts, data.Length);
    }

    /// <summary>Entropy of a stream, read in fixed-size chunks (any image size, constant memory).</summary>
    public static Result Compute(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<long> counts = stackalloc long[256];
        var buffer = new byte[1 << 16];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++) counts[buffer[i]]++;
            total += read;
        }
        return FromCounts(counts, total);
    }

    private static Result FromCounts(ReadOnlySpan<long> counts, long total)
    {
        if (total == 0) return new Result(0, 0);
        double n = total;
        double h = 0;
        for (int i = 0; i < 256; i++)
        {
            long c = counts[i];
            if (c == 0) continue;
            double p = c / n;
            h -= p * Math.Log2(p);
        }
        // Clamp tiny negative rounding to zero.
        return new Result(total, h < 0 ? 0 : h);
    }
}
