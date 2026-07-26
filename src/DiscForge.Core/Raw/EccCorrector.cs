// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Raw;

/// <summary>What the RSPC decoder managed on one sector.</summary>
public sealed record EccCorrectionResult
{
    /// <summary>True when the sector's EDC validates after correction — the
    /// independent confirmation that the repair was genuine.</summary>
    public required bool Success { get; init; }
    public required int BytesCorrected { get; init; }
    public required int PassesUsed { get; init; }
    /// <summary>Erased positions the decoder could not resolve.</summary>
    public required IReadOnlyList<int> StillUncertain { get; init; }
    public required bool EdcValid { get; init; }
    public required string Detail { get; init; }
}

/// <summary>
/// Corrects a damaged Mode 1 sector using the Reed-Solomon parity the disc
/// already carries, guided by the drive's C2 error pointers.
///
/// Why this recovers what re-reading cannot. Both RSPC codes carry two parity
/// symbols, which gives a minimum distance of three. Spent on errors at unknown
/// positions that buys a single correction per codeword; spent on ERASURES —
/// errors whose position is known — it buys two. C2 pointers supply exactly
/// those positions, so knowing where the damage is doubles what the parity can
/// repair. Damage that is physically permanent, that no number of re-reads will
/// shift, is often still fully correctable from bytes already in hand.
///
/// The interleave is what makes this work on real damage. P codewords step 86
/// bytes at a time through the sector, so a scratch producing a hundred
/// consecutive bad bytes puts at most two erasures in any one P codeword —
/// exactly at capacity, and correctable. Damage that would be hopeless if it
/// landed in one codeword is spread across eighty-six of them by design.
///
/// Iterating between P and Q compounds it further: bytes P repairs stop being
/// erasures for Q, which lets Q repair codewords that were previously over
/// capacity, which frees more for P. A sector too damaged for either code alone
/// can fall to the two together.
///
/// Mode 1 only. Mode 2 Form 1 computes its parity with the header zeroed and
/// would need that adjustment; Mode 2 Form 2 carries no ECC at all and cannot
/// be repaired this way — for those, C2 voting across re-reads is all there is.
/// </summary>
public static class EccCorrector
{
    /// <summary>Reverse of the exponent table, built from EdcEcc's own so the
    /// two can never drift apart.</summary>
    private static readonly byte[] GfLogTable = BuildLog();

    private static byte[] BuildLog()
    {
        var log = new byte[256];
        for (int i = 0; i < 255; i++) log[EdcEcc.GfPow(i)] = (byte)i;
        return log;
    }

    private static byte GfDiv(byte a, byte b)
    {
        if (b == 0) throw new DivideByZeroException("GF(2^8) division by zero.");
        if (a == 0) return 0;
        return EdcEcc.GfPow(GfLogTable[a] - GfLogTable[b] + 255);
    }

    private const int SectorBytes = 2352;
    private const int SyncBytes = 12;

    /// <summary>The fixed sync pattern every data sector begins with. Not
    /// covered by ECC — but it is the same on every sector ever pressed, so a
    /// damaged one is repaired by knowing rather than by decoding.</summary>
    private static ReadOnlySpan<byte> SyncPattern =>
        new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };

    /// <summary>
    /// Repair a raw Mode 1 sector in place.
    /// </summary>
    /// <param name="sector">2352 unscrambled bytes, modified in place.</param>
    /// <param name="erasures">Byte offsets the drive could not vouch for.</param>
    /// <param name="maxPasses">
    /// How many times to alternate P and Q. Each pass can only reduce the
    /// erasure count, so this terminates on its own; the limit is a guard, not
    /// a tuning knob.
    /// </param>
    /// <param name="correctUnflaggedErrors">
    /// Also repair single errors in codewords with no erasures at all. Worth
    /// having because C2 under-reports — drives miss bytes they got wrong — but
    /// it carries a small risk: a codeword with two unflagged errors looks
    /// exactly like one with a single error elsewhere, and "correcting" it makes
    /// matters worse. The EDC check at the end is what catches that.
    /// </param>
    public static EccCorrectionResult CorrectMode1(
        Span<byte> sector,
        IReadOnlyList<int> erasures,
        int maxPasses = 8,
        bool correctUnflaggedErrors = true)
    {
        if (sector.Length != SectorBytes)
            throw new ArgumentException($"A raw sector is {SectorBytes} bytes.", nameof(sector));
        ArgumentNullException.ThrowIfNull(erasures);

        var erased = new bool[SectorBytes];
        foreach (int e in erasures)
            if (e >= 0 && e < SectorBytes) erased[e] = true;

        int corrected = 0;

        // Sync first: it carries no parity, but it is a constant. A damaged sync
        // byte is repaired by writing what it must be.
        for (int i = 0; i < SyncBytes; i++)
        {
            if (!erased[i]) continue;
            if (sector[i] != SyncPattern[i]) { sector[i] = SyncPattern[i]; corrected++; }
            erased[i] = false;
        }

        int passes = 0;
        var offsets = new int[45];

        for (; passes < Math.Max(1, maxPasses); passes++)
        {
            int before = corrected;

            // P: 43 columns x 2 planes, 26 symbols each.
            for (int plane = 0; plane < 2; plane++)
                for (int col = 0; col < 43; col++)
                {
                    FillPOffsets(offsets, plane, col);
                    corrected += CorrectCodeword(sector, offsets.AsSpan(0, 26), erased,
                                                 correctUnflaggedErrors);
                }

            // Q: 26 diagonals x 2 planes, 45 symbols each. Q spans the P parity
            // as well, so a pass here can repair P's own parity bytes — which is
            // what lets the next P pass do more than the last.
            for (int plane = 0; plane < 2; plane++)
                for (int diag = 0; diag < 26; diag++)
                {
                    FillQOffsets(offsets, plane, diag);
                    corrected += CorrectCodeword(sector, offsets.AsSpan(0, 45), erased,
                                                 correctUnflaggedErrors);
                }

            if (corrected == before) { passes++; break; }   // nothing left to do
        }

        var remaining = new List<int>();
        for (int i = 0; i < SectorBytes; i++)
            if (erased[i]) remaining.Add(i);

        var (edcOk, _) = EdcEcc.VerifyMode1(sector);

        string detail = (edcOk, corrected, remaining.Count) switch
        {
            (true, 0, _) => "The sector was already intact.",
            (true, _, 0) => $"{corrected} byte(s) rebuilt from parity; EDC confirms the sector.",
            (true, _, _) => $"{corrected} byte(s) rebuilt and EDC confirms the sector, though " +
                            $"{remaining.Count} position(s) stayed flagged — the parity covered " +
                            "them even where the drive would not vouch for them.",
            (false, _, 0) => "Every flagged byte was resolved, but the EDC still fails: there is " +
                             "damage the drive never reported.",
            _ => $"{remaining.Count} position(s) remain beyond what the parity can rebuild — more " +
                 "than two erasures fell in the same codeword.",
        };

        return new EccCorrectionResult
        {
            Success = edcOk,
            BytesCorrected = corrected,
            PassesUsed = passes,
            StillUncertain = remaining,
            EdcValid = edcOk,
            Detail = detail,
        };
    }

    /// <summary>Byte offsets of one P codeword: 24 data symbols striding 86
    /// bytes through the sector, then its two parity symbols.</summary>
    private static void FillPOffsets(int[] into, int plane, int col)
    {
        for (int row = 0; row < 24; row++)
            into[row] = 12 + 2 * (col + 43 * row) + plane;
        into[24] = 12 + 2 * (1032 + col) + plane;
        into[25] = 12 + 2 * (1075 + col) + plane;
    }

    /// <summary>Byte offsets of one Q codeword: 43 data symbols on a diagonal
    /// through the 1118-word region — which includes P's parity — then its own
    /// two parity symbols.</summary>
    private static void FillQOffsets(int[] into, int plane, int diag)
    {
        for (int j = 0; j < 43; j++)
            into[j] = 12 + 2 * ((43 * diag + 44 * j) % 1118) + plane;
        into[43] = 12 + 2 * (1118 + diag) + plane;
        into[44] = 12 + 2 * (1144 + diag) + plane;
    }

    /// <summary>
    /// Decode one codeword, returning how many bytes it repaired.
    ///
    /// The generator's roots are α⁰ and α¹, so the two syndromes are the
    /// codeword evaluated at 1 and at α — matching EdcEcc.VerifyMode1's own
    /// check, deliberately, because a decoder that disagreed with the verifier
    /// about the convention would produce sectors that "corrected" into
    /// nonsense.
    /// </summary>
    private static int CorrectCodeword(Span<byte> sector, ReadOnlySpan<int> offsets,
                                       bool[] erased, bool correctUnflagged)
    {
        int n = offsets.Length;

        byte s0 = 0, s1 = 0;
        for (int i = 0; i < n; i++)
        {
            byte c = sector[offsets[i]];
            s0 ^= c;                                     // evaluated at α⁰ = 1
            s1 = (byte)(EdcEcc.GfMul(s1, 2) ^ c);        // Horner at α
        }

        // Where the erasures are, and how many.
        int e0 = -1, e1 = -1, count = 0;
        for (int i = 0; i < n; i++)
        {
            if (!erased[offsets[i]]) continue;
            if (count == 0) e0 = i;
            else if (count == 1) e1 = i;
            count++;
            if (count > 2) break;                        // over capacity; leave for another pass
        }

        if (s0 == 0 && s1 == 0)
        {
            // The codeword is consistent. Any erasures in it were false alarms —
            // the drive doubted bytes that were in fact correct. Clearing them
            // is what lets other codewords with genuine damage come back into
            // capacity on the next pass.
            if (count is 1 or 2)
            {
                if (e0 >= 0) erased[offsets[e0]] = false;
                if (e1 >= 0) erased[offsets[e1]] = false;
            }
            return 0;
        }

        switch (count)
        {
            case 2:
            {
                // Two erasures, two syndromes: an exact solve.
                //   s0 = a + b
                //   s1 = a·X0 + b·X1      where Xi = alpha^(n-1-i)
                byte x0 = EdcEcc.GfPow(n - 1 - e0);
                byte x1 = EdcEcc.GfPow(n - 1 - e1);
                byte denom = (byte)(x0 ^ x1);
                if (denom == 0) return 0;                // impossible for distinct positions

                byte a = GfDiv((byte)(s1 ^ EdcEcc.GfMul(s0, x1)), denom);
                byte b = (byte)(s0 ^ a);

                sector[offsets[e0]] ^= a;
                sector[offsets[e1]] ^= b;
                erased[offsets[e0]] = false;
                erased[offsets[e1]] = false;
                return (a != 0 ? 1 : 0) + (b != 0 ? 1 : 0);
            }

            case 1:
            {
                // One erasure: the error value is s0, and s1 must agree. If it
                // doesn't, something else in this codeword is also wrong and
                // "correcting" would corrupt a good byte.
                byte x = EdcEcc.GfPow(n - 1 - e0);
                if (EdcEcc.GfMul(s0, x) != s1) return 0;

                sector[offsets[e0]] ^= s0;
                erased[offsets[e0]] = false;
                return s0 != 0 ? 1 : 0;
            }

            case 0:
            {
                if (!correctUnflagged || s0 == 0) return 0;

                // No erasure flagged, yet the syndromes are non-zero: the drive
                // missed something. With one unknown error, X = s1/s0 gives its
                // position outright.
                byte x = GfDiv(s1, s0);
                int power = GfLogTable[x];
                int pos = n - 1 - power;
                if (pos < 0 || pos >= n) return 0;       // not a single error — leave it

                sector[offsets[pos]] ^= s0;
                return 1;
            }

            default:
                return 0;                                // three or more: over capacity
        }
    }
}