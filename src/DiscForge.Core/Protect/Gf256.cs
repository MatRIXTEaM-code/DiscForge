// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace DiscForge.Core.Protect;

/// <summary>
/// GF(2^8) arithmetic (primitive polynomial x^8+x^4+x^3+x^2+1, 0x11D — the usual Reed-Solomon field)
/// with a fast "dst ^= c · src" over whole buffers: SSSE3 nibble-table multiplication where available,
/// a 256-byte product row otherwise.
/// </summary>
public static class Gf256
{
    public static readonly byte[] Exp = new byte[512];
    public static readonly byte[] Log = new byte[256];
    private static readonly byte[][] MulRows = new byte[256][];
    private static readonly byte[][] LowNibble = new byte[256][];
    private static readonly byte[][] HighNibble = new byte[256][];

    static Gf256()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11D;
        }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
        for (int c = 0; c < 256; c++)
        {
            var row = new byte[256];
            for (int v = 0; v < 256; v++) row[v] = Mul((byte)c, (byte)v);
            MulRows[c] = row;
            var lo = new byte[16];
            var hi = new byte[16];
            for (int n = 0; n < 16; n++) { lo[n] = row[n]; hi[n] = row[n << 4]; }
            LowNibble[c] = lo;
            HighNibble[c] = hi;
        }
    }

    public static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    public static byte Div(byte a, byte b)
    {
        if (b == 0) throw new DivideByZeroException();
        return a == 0 ? (byte)0 : Exp[Log[a] + 255 - Log[b]];
    }

    public static byte Inv(byte a) => Div(1, a);

    /// <summary>α^e for any integer exponent.</summary>
    public static byte Pow(int e)
    {
        e %= 255;
        if (e < 0) e += 255;
        return Exp[e];
    }

    /// <summary>dst[i] ^= c · src[i].</summary>
    public static void MulAdd(Span<byte> dst, ReadOnlySpan<byte> src, byte c)
    {
        if (c == 0) return;
        int n = Math.Min(dst.Length, src.Length);
        int i = 0;
        if (c == 1)
        {
            for (; i + 16 <= n && Vector128.IsHardwareAccelerated; i += 16)
                (Vector128.Create((ReadOnlySpan<byte>)dst.Slice(i, 16)) ^ Vector128.Create(src.Slice(i, 16))).CopyTo(dst.Slice(i, 16));
            for (; i < n; i++) dst[i] ^= src[i];
            return;
        }
        if (Ssse3.IsSupported && n >= 16)
        {
            var lo = Vector128.Create(LowNibble[c]);
            var hi = Vector128.Create(HighNibble[c]);
            var mask = Vector128.Create((byte)0x0F);
            for (; i + 16 <= n; i += 16)
            {
                var s = Vector128.Create(src.Slice(i, 16));
                var l = Ssse3.Shuffle(lo, s & mask);
                var h = Ssse3.Shuffle(hi, Sse2.ShiftRightLogical(s.AsUInt16(), 4).AsByte() & mask);
                (Vector128.Create((ReadOnlySpan<byte>)dst.Slice(i, 16)) ^ l ^ h).CopyTo(dst.Slice(i, 16));
            }
        }
        var row = MulRows[c];
        for (; i < n; i++) dst[i] ^= row[src[i]];
    }

    /// <summary>dst[i] = c · src[i].</summary>
    public static void Mul(Span<byte> dst, ReadOnlySpan<byte> src, byte c)
    {
        dst.Clear();
        MulAdd(dst, src, c);
    }
}
