// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Raw;

/// <summary>
/// C2 error pointers for one sector: which of its 2352 bytes the drive could
/// not correct.
///
/// Why this is worth having. A drive's CIRC decoder fixes what it can and, when
/// it can't, normally reports nothing more useful than "read failed" — the
/// sector is opaque and the only recourse is to read it again and hope. C2
/// pointers change that: the drive says WHICH bytes it gave up on, so a
/// re-read's good bytes can be combined with the previous read's good bytes
/// instead of discarding both. Damage that no single read survives is often
/// recoverable across three or four, because the uncorrectable bytes move.
///
/// The catch is that C2 is advisory. A drive may under-report (bytes it silently
/// got wrong aren't flagged) and the bit ordering, while specified as MSB-first
/// in MMC, is worth verifying per drive. So C2 is treated here as evidence for
/// choosing between reads, never as proof a byte is correct.
/// </summary>
public sealed class C2ErrorMap
{
    /// <summary>Bytes in a raw CD sector's main channel.</summary>
    public const int SectorBytes = 2352;

    /// <summary>Bytes of C2 data: one bit per main-channel byte, 2352/8.</summary>
    public const int C2Bytes = 294;

    private readonly bool[] _bad = new bool[SectorBytes];

    /// <summary>How many bytes the drive could not correct.</summary>
    public int BadByteCount { get; private set; }

    /// <summary>True when the drive reported every byte as good.</summary>
    public bool Clean => BadByteCount == 0;

    /// <summary>True when the whole sector is flagged — usually means the drive
    /// gave up entirely rather than that every byte is individually damaged.</summary>
    public bool Total => BadByteCount == SectorBytes;

    public bool this[int byteIndex] => _bad[byteIndex];

    private C2ErrorMap() { }

    /// <summary>
    /// Parse a 294-byte C2 error block.
    ///
    /// MMC specifies MSB-first: bit 7 of the first C2 byte refers to byte 0 of
    /// the sector, bit 6 to byte 1, and so on. <paramref name="msbFirst"/> exists
    /// because that ordering is worth confirming against real hardware before
    /// being relied on — a reversed map would flag exactly the wrong bytes, and
    /// the symptom (recovery that makes sectors worse) is easy to misattribute.
    /// </summary>
    public static C2ErrorMap Parse(ReadOnlySpan<byte> c2Block, bool msbFirst = true)
    {
        if (c2Block.Length < C2Bytes)
            throw new ArgumentException(
                $"A C2 error block is {C2Bytes} bytes; got {c2Block.Length}.", nameof(c2Block));

        var map = new C2ErrorMap();
        for (int i = 0; i < SectorBytes; i++)
        {
            int bit = msbFirst ? 7 - (i & 7) : i & 7;
            if ((c2Block[i >> 3] & (1 << bit)) != 0)
            {
                map._bad[i] = true;
                map.BadByteCount++;
            }
        }
        return map;
    }

    /// <summary>A map with nothing flagged — for drives or reads with no C2 data.
    /// Deliberately optimistic: absence of C2 is not evidence of damage, and a
    /// read with no pointers should still be usable as a voting candidate.</summary>
    public static C2ErrorMap None() => new();

    /// <summary>Every byte flagged, for a read that failed outright.</summary>
    public static C2ErrorMap All()
    {
        var map = new C2ErrorMap();
        for (int i = 0; i < SectorBytes; i++) map._bad[i] = true;
        map.BadByteCount = SectorBytes;
        return map;
    }

    /// <summary>
    /// The bad bytes as contiguous runs — a scratch produces a few long runs,
    /// while marginal reflectivity produces many short ones. Useful in a report:
    /// "3 runs totalling 88 bytes" tells you more about a disc than "88 bytes".
    /// </summary>
    public IReadOnlyList<(int Start, int Length)> BadRuns()
    {
        var runs = new List<(int, int)>();
        int i = 0;
        while (i < SectorBytes)
        {
            if (!_bad[i]) { i++; continue; }
            int start = i;
            while (i < SectorBytes && _bad[i]) i++;
            runs.Add((start, i - start));
        }
        return runs;
    }

    /// <summary>
    /// Whether the damage falls inside the 2048-byte user data of a Mode 1
    /// sector. Errors confined to sync, header or ECC are recoverable by other
    /// means — the payload is intact and that is what an image needs.
    /// </summary>
    public bool AffectsMode1UserData()
    {
        for (int i = 16; i < 16 + 2048; i++)
            if (_bad[i]) return true;
        return false;
    }

    /// <summary>As above for Mode 2 Form 1, whose user data starts at 24.</summary>
    public bool AffectsMode2Form1UserData()
    {
        for (int i = 24; i < 24 + 2048; i++)
            if (_bad[i]) return true;
        return false;
    }

    public override string ToString()
    {
        if (Clean) return "clean";
        if (Total) return "entire sector unreadable";
        var runs = BadRuns();
        return $"{BadByteCount} byte(s) uncorrectable in {runs.Count} run(s)";
    }
}

/// <summary>
/// Combines several reads of one sector, using each read's C2 pointers to pick
/// bytes the drive believed it got right.
///
/// The principle: a byte the drive flags is one it knows it couldn't correct, so
/// prefer a read that doesn't flag it. Where several unflagged reads disagree —
/// which means at least one drive report was wrong — majority wins, and a tie is
/// recorded as uncertain rather than resolved arbitrarily. What comes out is a
/// sector plus an honest account of which bytes nobody could vouch for.
/// </summary>
public sealed class C2SectorVoter
{
    private readonly List<(byte[] Data, C2ErrorMap Map)> _reads = new();

    public int ReadCount => _reads.Count;

    public void Add(ReadOnlySpan<byte> sector, C2ErrorMap map)
    {
        if (sector.Length != C2ErrorMap.SectorBytes)
            throw new ArgumentException(
                $"A raw sector is {C2ErrorMap.SectorBytes} bytes; got {sector.Length}.",
                nameof(sector));
        _reads.Add((sector.ToArray(), map));
    }

    /// <summary>
    /// True when the most recent read had no C2 errors — no point re-reading a
    /// sector the drive is already happy with.
    /// </summary>
    public bool LastReadWasClean => _reads.Count > 0 && _reads[^1].Map.Clean;

    /// <summary>
    /// True when every byte has at least one read that didn't flag it, so a
    /// complete sector can be assembled and further reads would add nothing.
    /// </summary>
    public bool FullyCovered()
    {
        if (_reads.Count == 0) return false;
        for (int i = 0; i < C2ErrorMap.SectorBytes; i++)
        {
            bool covered = false;
            foreach (var (_, map) in _reads)
                if (!map[i]) { covered = true; break; }
            if (!covered) return false;
        }
        return true;
    }

    public sealed record Result(
        byte[] Sector,
        IReadOnlyList<int> UncertainBytes,
        int ReadsUsed,
        int BytesFromVoting)
    {
        /// <summary>True when every byte came from at least one read that didn't
        /// flag it. The sector is as good as this drive can attest.</summary>
        public bool Complete => UncertainBytes.Count == 0;
    }

    /// <summary>
    /// Assemble the best sector the reads support.
    ///
    /// Per byte: consider only reads that didn't flag it. None — the byte is
    /// uncertain, and the last read's value stands so the sector is at least
    /// well-formed. One — take it. Several that agree — take it. Several that
    /// disagree — majority, and if tied, mark uncertain and take the first, since
    /// an arbitrary choice recorded as arbitrary is honest while an arbitrary
    /// choice recorded as certain is not.
    /// </summary>
    public Result Vote()
    {
        if (_reads.Count == 0)
            throw new InvalidOperationException("No reads to combine.");

        var sector = new byte[C2ErrorMap.SectorBytes];
        var uncertain = new List<int>();
        int voted = 0;

        Span<byte> values = stackalloc byte[16];
        Span<int> counts = stackalloc int[16];

        for (int i = 0; i < C2ErrorMap.SectorBytes; i++)
        {
            int distinct = 0;
            foreach (var (data, map) in _reads)
            {
                if (map[i]) continue;                 // the drive disowned this byte
                byte v = data[i];
                int j = 0;
                for (; j < distinct; j++)
                    if (values[j] == v) { counts[j]++; break; }
                if (j == distinct && distinct < values.Length)
                {
                    values[distinct] = v;
                    counts[distinct] = 1;
                    distinct++;
                }
            }

            if (distinct == 0)
            {
                // No read vouched for this byte. Keep the latest value so the
                // sector stays well-formed, and say so.
                sector[i] = _reads[^1].Data[i];
                uncertain.Add(i);
                continue;
            }

            int best = 0;
            for (int j = 1; j < distinct; j++)
                if (counts[j] > counts[best]) best = j;

            bool tied = false;
            for (int j = 0; j < distinct; j++)
                if (j != best && counts[j] == counts[best]) tied = true;

            sector[i] = values[best];
            if (distinct > 1) voted++;
            if (tied) uncertain.Add(i);
        }

        return new Result(sector, uncertain, _reads.Count, voted);
    }
}