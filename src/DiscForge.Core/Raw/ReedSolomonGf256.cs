// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Raw;

/// <summary>
/// A general Reed-Solomon codec over GF(2^8) (primitive polynomial 0x11D, generator α = 2) with an
/// errors-and-erasures decoder — the mathematical core of a CD's CIRC error correction. C1 is RS(32,28)
/// and C2 is RS(28,24); both are instances of this. It encodes a k-byte message to an n-byte codeword and
/// decodes one back, correcting up to (n−k)/2 unknown errors, or up to (n−k) erasures whose positions are
/// known (as they are after the C1 stage flags them for C2). Systematic encoding, syndromes,
/// Berlekamp-Massey, Chien search and Forney's algorithm — the classic pipeline, polynomials stored
/// highest-degree-first.
/// </summary>
public sealed class ReedSolomonGf256
{
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static ReedSolomonGf256()
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
    }

    public int N { get; }
    public int K { get; }
    public int NSym => N - K;
    private readonly byte[] _gen;

    public ReedSolomonGf256(int n, int k)
    {
        if (n <= k || n > 255 || k < 1) throw new ArgumentException("Require 1 <= k < n <= 255.");
        N = n; K = k;
        _gen = GeneratorPoly(NSym);
    }

    /// <summary>Encode a k-byte message to an n-byte systematic codeword (message then parity).</summary>
    public byte[] Encode(ReadOnlySpan<byte> message)
    {
        if (message.Length != K) throw new ArgumentException($"Message must be {K} bytes.", nameof(message));
        var work = new byte[N];
        message.CopyTo(work);
        for (int i = 0; i < K; i++)
        {
            byte coef = work[i];
            if (coef != 0)
                for (int j = 1; j < _gen.Length; j++)
                    work[i + j] ^= Mul(_gen[j], coef);
        }
        var outp = new byte[N];
        message.CopyTo(outp);
        Array.Copy(work, K, outp, K, NSym);
        return outp;
    }

    /// <summary>Decode an n-byte codeword, correcting errors and (optionally) known erasures. Returns
    /// true with the corrected bytes; false if the damage exceeds the code's capacity.</summary>
    public bool TryDecode(byte[] codeword, out byte[] corrected, IReadOnlyList<int>? erasures = null)
    {
        if (codeword.Length != N) throw new ArgumentException($"Codeword must be {N} bytes.", nameof(codeword));
        corrected = (byte[])codeword.Clone();

        var erasePos = erasures?.Where(p => p >= 0 && p < N).Distinct().ToList() ?? new List<int>();
        if (erasePos.Count > NSym) return false;
        foreach (var e in erasePos) corrected[e] = 0;

        var synd = CalcSyndromes(corrected);                 // length NSym+1, synd[0] == 0
        if (synd.Max() == 0) return true;

        var fsynd = ForneySyndromes(synd, erasePos);         // length NSym
        var errLoc = FindErrorLocator(fsynd, erasePos.Count);
        Array.Reverse(errLoc);
        var errPos = FindErrors(errLoc);
        if (errPos is null) return false;

        var allPos = erasePos.Concat(errPos).Distinct().ToList();
        if (allPos.Count > NSym) return false;
        if (!CorrectErrata(corrected, synd, allPos)) return false;

        return CalcSyndromes(corrected).Max() == 0;          // verify
    }

    // ---- GF arithmetic ------------------------------------------------------

    private static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];
    private static byte Div(byte a, byte b) => a == 0 ? (byte)0 : Exp[(Log[a] + 255 - Log[b]) % 255];
    private static byte Pow(byte x, int p) => Exp[(Log[x] * (((p % 255) + 255) % 255)) % 255];
    private static byte Inv(byte x) => Exp[255 - Log[x]];

    private static byte[] PolyScale(byte[] p, byte s)
    {
        var r = new byte[p.Length];
        for (int i = 0; i < p.Length; i++) r[i] = Mul(p[i], s);
        return r;
    }

    private static byte[] PolyAdd(byte[] a, byte[] b)
    {
        var r = new byte[Math.Max(a.Length, b.Length)];
        for (int i = 0; i < a.Length; i++) r[i + r.Length - a.Length] = a[i];
        for (int i = 0; i < b.Length; i++) r[i + r.Length - b.Length] ^= b[i];
        return r;
    }

    private static byte[] PolyMul(byte[] p, byte[] q)
    {
        var r = new byte[p.Length + q.Length - 1];
        for (int i = 0; i < p.Length; i++)
            for (int j = 0; j < q.Length; j++)
                r[i + j] ^= Mul(p[i], q[j]);
        return r;
    }

    private static byte PolyEval(byte[] poly, byte x)
    {
        byte y = poly[0];
        for (int i = 1; i < poly.Length; i++) y = (byte)(Mul(y, x) ^ poly[i]);
        return y;
    }

    private static byte[] GeneratorPoly(int nsym)
    {
        var g = new byte[] { 1 };
        for (int i = 0; i < nsym; i++) g = PolyMul(g, new byte[] { 1, Pow(2, i) });
        return g;
    }

    // ---- decoder stages (faithful to the canonical algorithm) ---------------

    private byte[] CalcSyndromes(byte[] msg)
    {
        var s = new byte[NSym + 1];
        for (int i = 0; i < NSym; i++) s[i + 1] = PolyEval(msg, Pow(2, i));
        return s;   // s[0] left 0
    }

    private byte[] ForneySyndromes(byte[] synd, List<int> erasePos)
    {
        var fsynd = synd.Skip(1).ToArray();      // drop the leading 0 → length NSym
        foreach (var p in erasePos)
        {
            byte x = Pow(2, N - 1 - p);
            for (int j = 0; j < fsynd.Length - 1; j++)
                fsynd[j] = (byte)(Mul(fsynd[j], x) ^ fsynd[j + 1]);
        }
        return fsynd;
    }

    private byte[] FindErrorLocator(byte[] synd, int eraseCount)
    {
        var errLoc = new byte[] { 1 };
        var oldLoc = new byte[] { 1 };
        int syndShift = synd.Length > NSym ? synd.Length - NSym : 0;

        for (int i = 0; i < NSym - eraseCount; i++)
        {
            int k = i + syndShift;
            byte delta = synd[k];
            for (int j = 1; j < errLoc.Length; j++)
                delta ^= Mul(errLoc[errLoc.Length - 1 - j], synd[k - j]);

            oldLoc = Append(oldLoc, 0);
            if (delta != 0)
            {
                if (oldLoc.Length > errLoc.Length)
                {
                    var newLoc = PolyScale(oldLoc, delta);
                    oldLoc = PolyScale(errLoc, Inv(delta));
                    errLoc = newLoc;
                }
                errLoc = PolyAdd(errLoc, PolyScale(oldLoc, delta));
            }
        }
        int start = 0; while (start < errLoc.Length - 1 && errLoc[start] == 0) start++;
        return errLoc.Skip(start).ToArray();
    }

    private List<int>? FindErrors(byte[] errLoc)
    {
        int errs = errLoc.Length - 1;
        var pos = new List<int>();
        for (int i = 0; i < N; i++)
            if (PolyEval(errLoc, Pow(2, i)) == 0)
                pos.Add(N - 1 - i);
        return pos.Count != errs ? null : pos;
    }

    private bool CorrectErrata(byte[] msg, byte[] synd, List<int> errPos)
    {
        var coordPos = errPos.Select(p => N - 1 - p).ToList();

        // Errata locator from the positions.
        var errLoc = new byte[] { 1 };
        foreach (var p in coordPos)
            errLoc = PolyMul(errLoc, PolyAdd(new byte[] { 1 }, new byte[] { Pow(2, p), 0 }));

        // Error evaluator Ω(x) = remainder of (reverse(synd) · errLoc) by x^(len(errLoc)).
        var syndRev = synd.Reverse().ToArray();
        var prod = PolyMul(syndRev, errLoc);
        int keep = errLoc.Length;                    // = number of errata + 1... use len-1 like reference
        // reference: remainder = last (len(err_loc)-1 + 1) coefficients → keep = errLoc.Length
        var errEval = prod.Skip(Math.Max(0, prod.Length - keep)).ToArray();
        Array.Reverse(errEval);

        // Chien magnitudes X = α^(coordPos).
        var X = coordPos.Select(p => Pow(2, p)).ToArray();

        var e = new byte[msg.Length];
        for (int i = 0; i < X.Length; i++)
        {
            byte xi = X[i];
            byte xiInv = Inv(xi);

            byte denom = 1;
            for (int j = 0; j < X.Length; j++)
                if (j != i) denom = Mul(denom, (byte)(1 ^ Mul(xiInv, X[j])));
            if (denom == 0) return false;

            var errEvalRev = errEval.Reverse().ToArray();
            byte y = PolyEval(errEvalRev, xiInv);
            y = Mul(xi, y);

            byte magnitude = Div(y, denom);
            e[errPos[i]] = magnitude;
        }

        var fixedMsg = PolyAdd(msg, e);
        Array.Copy(fixedMsg, fixedMsg.Length - msg.Length, msg, 0, msg.Length);
        return true;
    }

    private static byte[] Append(byte[] p, byte v)
    {
        var r = new byte[p.Length + 1];
        Array.Copy(p, r, p.Length);
        r[^1] = v;
        return r;
    }
}
