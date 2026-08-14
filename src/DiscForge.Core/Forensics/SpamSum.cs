// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;

namespace DiscForge.Core.Forensics;

/// <summary>
/// Context-triggered piecewise ("fuzzy") hashing — the SpamSum algorithm (Andrew Tridgell), which is
/// the basis of ssdeep and the fuzzy hash Aaru records. Unlike a cryptographic hash, where one
/// changed byte scrambles the whole digest, a fuzzy hash of two SIMILAR inputs stays similar, so
/// <see cref="Compare"/> scores how alike two images are (0..100). For preservation that means
/// spotting two rips of the same disc that differ only in a bad sector, a re-encode, or padding —
/// a same/near-miss judgement a SHA-256 can never give.
///
/// The signature is <c>blocksize:hash1:hash2</c>. This is a clean-room implementation of the public
/// algorithm, validated byte-for-byte against reference ssdeep signatures (and score-for-score
/// against the reference comparison) by unit-test vectors, so signatures and scores interchange with
/// other ssdeep-compatible tools.
/// </summary>
public static class SpamSum
{
    private const int SpamSumLength = 64;
    private const uint MinBlocksize = 3;
    private const int RollingWindow = 7;
    private const uint HashInit = 0x28021967;
    private const uint HashPrime = 0x01000193;
    private const string B64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    /// <summary>Compute the fuzzy-hash signature "blocksize:hash1:hash2" of <paramref name="data"/>.</summary>
    public static string Hash(ReadOnlySpan<byte> data)
    {
        uint blockSize = MinBlocksize;
        while (blockSize * SpamSumLength < data.Length) blockSize *= 2;

        while (true)
        {
            var d = Digest(data, blockSize);
            // The halving decision uses the PRE-final-flush length, exactly as ssdeep does.
            if (blockSize > MinBlocksize && d.Sig1.Length < SpamSumLength / 2)
            {
                blockSize /= 2;
                continue;
            }
            // Final flush: when the last rolling hash is nonzero the in-progress piece is appended;
            // when it is exactly zero, the char the full signature could not take earlier stands in.
            string s1 = d.Sig1, s2 = d.Sig2;
            if (d.LastRoll != 0)
            {
                s1 += B64[(int)(d.H1 % 64)];
                s2 += B64[(int)(d.H2 % 64)];
            }
            else
            {
                s1 += d.LastChar1;
                s2 += d.LastChar2;
            }
            return $"{blockSize}:{s1}:{s2}";
        }
    }

    private readonly record struct DigestResult(string Sig1, string Sig2, uint H1, uint H2,
                                                uint LastRoll, string LastChar1, string LastChar2);

    private static DigestResult Digest(ReadOnlySpan<byte> data, uint blockSize)
    {
        var sig1 = new StringBuilder();
        var sig2 = new StringBuilder();
        uint h1 = HashInit, h2 = HashInit;               // traditional hashes for blockSize and 2×blockSize
        string lastChar1 = "", lastChar2 = "";

        Span<byte> window = stackalloc byte[RollingWindow];
        window.Clear();
        uint r1 = 0, r2 = 0, r3 = 0;
        int rn = 0;
        uint roll = 0;

        foreach (byte c in data)
        {
            // Traditional (FNV-style) hashes.
            h1 = (h1 * HashPrime) ^ c;
            h2 = (h2 * HashPrime) ^ c;

            // Rolling hash over the last ROLLING_WINDOW bytes.
            r2 = r2 - r1 + (uint)(RollingWindow * c);
            r1 = r1 + c - window[rn];
            window[rn] = c;
            rn = (rn + 1) % RollingWindow;
            r3 = (r3 << 5) ^ c;
            roll = r1 + r2 + r3;

            if (roll % blockSize == blockSize - 1)
            {
                // A full signature can't take another char, but remembers what it WOULD have been:
                // that char becomes the closer if the stream ends with a zero rolling hash.
                lastChar1 = B64[(int)(h1 % 64)].ToString();
                if (sig1.Length < SpamSumLength - 1)
                {
                    sig1.Append(B64[(int)(h1 % 64)]);
                    h1 = HashInit;
                    lastChar1 = "";
                }
                if (roll % (blockSize * 2) == blockSize * 2 - 1)
                {
                    lastChar2 = B64[(int)(h2 % 64)].ToString();
                    if (sig2.Length < SpamSumLength / 2 - 1)
                    {
                        sig2.Append(B64[(int)(h2 % 64)]);
                        h2 = HashInit;
                        lastChar2 = "";
                    }
                }
            }
        }

        return new DigestResult(sig1.ToString(), sig2.ToString(), h1, h2, roll, lastChar1, lastChar2);
    }

    /// <summary>Similarity of two SpamSum signatures, 0 (nothing alike) to 100 (identical), following
    /// the ssdeep comparison exactly: sequence stripping, the 7-char common-substring gate, the
    /// integer-division score pipeline and the small-blocksize cap — so scores agree with other
    /// ssdeep-compatible tools. Signatures whose block sizes are not equal or 2× apart score 0.</summary>
    public static int Compare(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var (ba, a1r, a2r) = Parse(a);
        var (bb, b1r, b2r) = Parse(b);
        if (ba == 0 || bb == 0) return 0;
        if (ba != bb && ba != bb * 2 && bb != ba * 2) return 0;

        // Runs of >3 identical chars carry no information (a constant region triggers repeatedly);
        // strip them before scoring, as ssdeep does.
        string a1 = StripSequences(a1r), a2 = StripSequences(a2r);
        string b1 = StripSequences(b1r), b2 = StripSequences(b2r);

        if (ba == bb && a1 == b1) return 100;

        if (ba == bb) return Math.Max(ScoreStrings(a1, b1, ba), ScoreStrings(a2, b2, ba * 2));
        if (ba == bb * 2) return ScoreStrings(a1, b2, ba);
        return ScoreStrings(a2, b1, bb);
    }

    private static (uint block, string s1, string s2) Parse(string sig)
    {
        var parts = sig.Split(':');
        if (parts.Length != 3 || !uint.TryParse(parts[0], out uint block)) return (0, "", "");
        return (block, parts[1], parts[2]);
    }

    /// <summary>Collapse any run of more than three identical characters to three.</summary>
    internal static string StripSequences(string s)
    {
        if (s.Length <= 3) return s;
        var sb = new StringBuilder(s.Length);
        sb.Append(s, 0, 3);
        for (int i = 3; i < s.Length; i++)
            if (s[i] != s[i - 1] || s[i] != s[i - 2] || s[i] != s[i - 3])
                sb.Append(s[i]);
        return sb.ToString();
    }

    /// <summary>Do the two strings share a common substring of at least the rolling-window length?
    /// Without one, an edit-distance similarity is coincidence, so ssdeep scores it 0.</summary>
    private static bool HasCommonSubstring(string s1, string s2)
    {
        for (int i = 0; i < s1.Length; i++)
            for (int j = 0; j < s2.Length; j++)
            {
                int cur = 0;
                while (i + cur < s1.Length && j + cur < s2.Length && s1[i + cur] == s2[j + cur]) cur++;
                if (cur >= RollingWindow) return true;
            }
        return false;
    }

    /// <summary>The ssdeep score: edit distance mapped through the reference integer pipeline, then
    /// capped for small block sizes so short signatures can't overstate similarity.</summary>
    private static int ScoreStrings(string s1, string s2, uint blockSize)
    {
        if (s1.Length > SpamSumLength || s2.Length > SpamSumLength) return 0;   // foreign over-long signature
        if (!HasCommonSubstring(s1, s2)) return 0;
        long score = Levenshtein(s1, s2);
        score = score * SpamSumLength / (s1.Length + s2.Length);
        score = 100 * score / SpamSumLength;
        score = 100 - score;
        long cap = blockSize / MinBlocksize * Math.Min(s1.Length, s2.Length);
        if (score > cap) score = cap;
        return (int)score;
    }

    // ssdeep's edit_distn: insert/delete cost 1, SUBSTITUTION COST 2 (equivalent to len1+len2−2·LCS).
    // The cost-2 substitution is what makes scores match real ssdeep — a plain cost-1 Levenshtein
    // systematically overstates similarity (verified against ssdeep 2.14.1).
    private static int Levenshtein(string a, string b)
    {
        int n = a.Length, m = b.Length;
        if (n == 0) return m;
        if (m == 0) return n;
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;
        for (int i = 1; i <= n; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 2;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[m];
    }
}
