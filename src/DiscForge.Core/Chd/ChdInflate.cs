// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Chd;

/// <summary>
/// A raw-DEFLATE (RFC 1951) decompressor that also reports the byte offset just
/// past the stream it consumed. That last part is why it exists rather than
/// <see cref="System.IO.Compression.DeflateStream"/>: a CHD stores each hunk's
/// two deflate sub-streams back to back with no length prefix on the second, so
/// walking the hunks means knowing exactly where one deflate stream ends and the
/// next begins. .NET's decompressor over-reads its input buffer and can't tell
/// you that; this tracks it precisely.
///
/// Clean-room, from RFC 1951. Validated byte-for-byte against a real chdman
/// image (the decompressed result matches the SHA-1 the CHD stores of itself).
/// </summary>
internal sealed class ChdInflate
{
    private readonly byte[] _d;
    private int _pos;
    private uint _bit;
    private int _nbits;

    public ChdInflate(byte[] data, int pos) { _d = data; _pos = pos; }

    /// <summary>Byte offset just past the consumed stream (valid after <see cref="Run"/>).</summary>
    public int NextOffset => _pos;

    private int Bit()
    {
        if (_nbits == 0)
        {
            // Reading past the end of the data means the DEFLATE stream is corrupt or
            // truncated — throw rather than feed endless zero bits (which would spin
            // forever producing literals for a stream whose final block never arrives).
            if (_pos >= _d.Length)
                throw new ChdFormatException("DEFLATE stream ran past the end of the CHD hunk (corrupt or truncated).");
            _bit = _d[_pos++]; _nbits = 8;
        }
        int b = (int)(_bit & 1); _bit >>= 1; _nbits--; return b;
    }
    private int Bits(int n) { int v = 0; for (int i = 0; i < n; i++) v |= Bit() << i; return v; }
    private void AlignByte() { _nbits = 0; }

    private static readonly int[] LenBase =
        { 3,4,5,6,7,8,9,10,11,13,15,17,19,23,27,31,35,43,51,59,67,83,99,115,131,163,195,227,258 };
    private static readonly int[] LenExtra =
        { 0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2,3,3,3,3,4,4,4,4,5,5,5,5,0 };
    private static readonly int[] DistBase =
        { 1,2,3,4,5,7,9,13,17,25,33,49,65,97,129,193,257,385,513,769,1025,1537,2049,3073,4097,6145,8193,12289,16385,24577 };
    private static readonly int[] DistExtra =
        { 0,0,0,0,1,1,2,2,3,3,4,4,5,5,6,6,7,7,8,8,9,9,10,10,11,11,12,12,13,13 };
    private static readonly int[] ClOrder = { 16,17,18,0,8,7,9,6,10,5,11,4,12,3,13,2,14,1,15 };

    private sealed class Tree { public int[] Count = new int[16]; public int[] Sym = Array.Empty<int>(); }

    private static Tree Build(int[] lengths, int n)
    {
        var t = new Tree { Sym = new int[n] };
        for (int i = 0; i < n; i++) t.Count[lengths[i]]++;
        t.Count[0] = 0;
        var offs = new int[16];
        int s = 0;
        for (int i = 1; i < 16; i++) { offs[i] = s; s += t.Count[i]; }
        var idx = (int[])offs.Clone();
        for (int i = 0; i < n; i++) if (lengths[i] != 0) t.Sym[idx[lengths[i]]++] = i;
        return t;
    }

    private int Decode(Tree t)
    {
        int code = 0, first = 0, index = 0;
        for (int len = 1; len < 16; len++)
        {
            code |= Bit();
            int cnt = t.Count[len];
            if (code - first < cnt) return t.Sym[index + (code - first)];
            index += cnt; first += cnt; first <<= 1; code <<= 1;
        }
        throw new ChdFormatException("Corrupt DEFLATE stream in a CHD hunk.");
    }

    /// <summary>Inflate one raw-deflate stream; leaves <see cref="NextOffset"/> at the
    /// next byte boundary (CHD hunk sub-streams are byte-aligned).</summary>
    public byte[] Run()
    {
        var outb = new List<byte>(1 << 16);
        int final;
        do
        {
            final = Bit();
            int type = Bits(2);
            if (type == 0)
            {
                AlignByte();
                if (_pos + 4 > _d.Length)
                    throw new ChdFormatException("Truncated stored DEFLATE block in a CHD hunk.");
                int len = _d[_pos] | (_d[_pos + 1] << 8);
                _pos += 4;                                  // len + nlen
                if (_pos + len > _d.Length)
                    throw new ChdFormatException("Stored DEFLATE block runs past the end of the CHD hunk.");
                for (int i = 0; i < len; i++) outb.Add(_d[_pos++]);
            }
            else if (type == 3)
                throw new ChdFormatException("Invalid DEFLATE block type in a CHD hunk.");
            else
            {
                Tree lit, dist;
                if (type == 1)
                {
                    var ll = new int[288];
                    for (int i = 0; i < 144; i++) ll[i] = 8;
                    for (int i = 144; i < 256; i++) ll[i] = 9;
                    for (int i = 256; i < 280; i++) ll[i] = 7;
                    for (int i = 280; i < 288; i++) ll[i] = 8;
                    lit = Build(ll, 288);
                    var dl = new int[30];
                    for (int i = 0; i < 30; i++) dl[i] = 5;
                    dist = Build(dl, 30);
                }
                else
                {
                    int hlit = Bits(5) + 257, hdist = Bits(5) + 1, hclen = Bits(4) + 4;
                    var cl = new int[19];
                    for (int i = 0; i < hclen; i++) cl[ClOrder[i]] = Bits(3);
                    var clt = Build(cl, 19);
                    var all = new int[hlit + hdist];
                    int p = 0;
                    while (p < hlit + hdist)
                    {
                        int sym = Decode(clt);
                        if (sym < 16) all[p++] = sym;
                        else if (sym == 16)
                        {
                            if (p == 0) throw new ChdFormatException("Corrupt DEFLATE code-length table in a CHD hunk.");
                            int r = Bits(2) + 3; int prev = all[p - 1];
                            while (r-- > 0) { if (p >= all.Length) throw new ChdFormatException("Corrupt DEFLATE code-length run in a CHD hunk."); all[p++] = prev; }
                        }
                        else if (sym == 17) { int r = Bits(3) + 3; while (r-- > 0) { if (p >= all.Length) throw new ChdFormatException("Corrupt DEFLATE code-length run in a CHD hunk."); all[p++] = 0; } }
                        else { int r = Bits(7) + 11; while (r-- > 0) { if (p >= all.Length) throw new ChdFormatException("Corrupt DEFLATE code-length run in a CHD hunk."); all[p++] = 0; } }
                    }
                    var litl = new int[hlit]; Array.Copy(all, 0, litl, 0, hlit);
                    var distl = new int[hdist]; Array.Copy(all, hlit, distl, 0, hdist);
                    lit = Build(litl, hlit);
                    dist = Build(distl, hdist);
                }

                while (true)
                {
                    int sym = Decode(lit);
                    if (sym == 256) break;
                    if (sym < 256) outb.Add((byte)sym);
                    else
                    {
                        sym -= 257;
                        int length = LenBase[sym] + Bits(LenExtra[sym]);
                        int ds = Decode(dist);
                        int distance = DistBase[ds] + Bits(DistExtra[ds]);
                        int srcpos = outb.Count - distance;
                        if (srcpos < 0) throw new ChdFormatException("Bad back-reference in a CHD hunk.");
                        for (int i = 0; i < length; i++) outb.Add(outb[srcpos + i]);
                    }
                }
            }
        } while (final == 0);

        AlignByte();
        return outb.ToArray();
    }
}
