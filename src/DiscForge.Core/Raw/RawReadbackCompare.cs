// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Raw;

/// <summary>
/// Proves a RAW burn actually landed on disc, byte for byte — the verification
/// ImgBurn never had. ImgBurn's "Verify" reads the burned disc and MD5s the
/// user data; it cannot check the sub-channel at all, because it never writes
/// one. DiscForge writes the whole 2352 main channel *and* the 96-byte
/// sub-channel with <see cref="RawImageGenerator"/>, so after a burn it can read
/// the disc back raw and compare against the exact bytes it sent — main channel,
/// EDC/ECC, and every Q frame — and say precisely what, if anything, the drive
/// changed.
///
/// This runs entirely on two images (the golden generated image and a raw
/// read-back capture); it needs no hardware and is what makes the burn-day
/// result a proof instead of a hope. See docs/RAW_DAO.md for the protocol.
///
/// Alignment: the two captures need not start at the same place — a read-back
/// often omits the drive-owned lead-in, or starts at a different base address.
/// Both program areas are contiguous and ascending, so the comparator finds
/// each one's program start (the lead-in boundary) and the absolute address
/// there, then walks the two in lock-step over their overlapping absolute range.
/// </summary>
public static class RawReadbackCompare
{
    /// <summary>How a per-sector difference is judged.</summary>
    public enum Severity { Defect, Warning }

    public sealed record Diff(long AbsoluteSector, string Category, Severity Severity, string Detail);

    public enum Grade { Pass, PassWithNotes, Fail }

    public sealed record Report
    {
        public required Grade Result { get; init; }
        public required long SectorsCompared { get; init; }
        public required long MainMismatches { get; init; }
        public required long EdcBroken { get; init; }
        /// <summary>Data sectors whose raw 2352 differed only because the drive returned
        /// them descrambled (they matched byte-for-byte once scramble state was normalized) —
        /// a read-path representation difference, not a burn defect.</summary>
        public long ScrambleNormalized { get; init; }
        public required long SubMismatches { get; init; }
        public required long MisAddressed { get; init; }
        public required long ProtectionLosses { get; init; }
        public required long SubTimingOnly { get; init; }
        /// <summary>Sectors where the golden Q was fine but the READ-BACK's own Q frame
        /// failed its own CRC — a transient sub-channel read error (real optical media takes
        /// the occasional single-frame glitch), not evidence the disc holds a wrong address.
        /// Distinct from <see cref="MisAddressed"/>, which requires the read-back's Q to be
        /// internally valid AND decode to the wrong place.</summary>
        public long SubReadNoise { get; init; }
        public required long Dropouts { get; init; }
        /// <summary>The first handful of each kind of difference, for a readable report.</summary>
        public required IReadOnlyList<Diff> Examples { get; init; }
        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

        public string Summary => SectorsCompared == 0
            ? "FAIL — no sectors were compared: the read-back is empty or does not overlap the golden " +
              "(a failed or zero-length read-back is not a pass)."
            : Result switch
        {
            Grade.Pass => $"PASS — all {SectorsCompared:N0} compared sectors are byte-identical " +
                          "on the disc (main channel + sub-channel).",
            Grade.PassWithNotes => $"PASS (with notes) — user data, addressing and protection are intact " +
                          $"across {SectorsCompared:N0} sectors; benign, drive-introduced differences only " +
                          $"({SubTimingOnly:N0} sub-timing, {ScrambleNormalized:N0} descrambled-on-read, " +
                          $"{SubReadNoise:N0} sub-read-noise) — see notes.",
            _ => $"FAIL — {MainMismatches + MisAddressed + ProtectionLosses + Dropouts:N0} defect(s) across " +
                 $"{SectorsCompared:N0} sectors (main {MainMismatches:N0}, mis-addressed {MisAddressed:N0}, " +
                 $"protection-loss {ProtectionLosses:N0}, dropout {Dropouts:N0}).",
        };
    }

    private const int MainSize = 2352;
    private const int MaxExamplesPerCategory = 8;

    /// <summary>
    /// Diagnostic-only: runs exactly the alignment setup <see cref="Compare"/> does (layout
    /// detection, lead-in boundary, header-vs-Q base address, skew correction) and reports every
    /// intermediate value instead of comparing sectors. For working out WHY an alignment landed
    /// where it did on a real capture, without re-deriving the algorithm by hand.
    /// </summary>
    public static string DebugAlignment(Stream golden, Stream readback)
    {
        var (gSize, gForm) = RawImageInspector.DetectLayout(golden);
        var (rSize, rForm) = RawImageInspector.DetectLayout(readback);
        int gSub = gSize - MainSize, rSub = rSize - MainSize;
        long gTotal = golden.Length / gSize, rTotal = readback.Length / rSize;

        long gProg = gForm is null ? 0 : RawImageInspector.FindLeadInLength(golden, gSize, gForm.Value);
        long rProg = rForm is null ? 0 : RawImageInspector.FindLeadInLength(readback, rSize, rForm.Value);

        long gBaseAbs = MainChannelBaseAbs(golden, gSize, gProg, out bool gAddr);
        string gSrc = gAddr ? "header" : "header-FAILED";
        if (!gAddr) { gBaseAbs = ProgramBaseAbs(golden, gSize, gForm, gSub, gProg, out gAddr); gSrc = gAddr ? "Q-fallback" : "NONE"; }
        long rBaseAbs = MainChannelBaseAbs(readback, rSize, rProg, out bool rAddr);
        string rSrc = rAddr ? "header" : "header-FAILED";
        if (!rAddr) { rBaseAbs = ProgramBaseAbs(readback, rSize, rForm, rSub, rProg, out rAddr); rSrc = rAddr ? "Q-fallback" : "NONE"; }
        bool byAddress = gAddr && rAddr;

        long startAbs = byAddress ? Math.Max(gBaseAbs, rBaseAbs) : 0;
        long gStartIdx = gProg + (byAddress ? startAbs - gBaseAbs : 0);
        long rStartIdx = rProg + (byAddress ? startAbs - rBaseAbs : 0);

        long gQBase = ProgramBaseAbs(golden, gSize, gForm, gSub, gProg, out bool gQAddr);
        long rQBase = ProgramBaseAbs(readback, rSize, rForm, rSub, rProg, out bool rQAddr);
        long gSkew = gQAddr ? gQBase - gBaseAbs : 0;
        long rSkew = rQAddr ? rQBase - rBaseAbs : 0;

        return $"gSize={gSize} gForm={gForm} gTotal={gTotal} gProg={gProg} gBaseAbs={gBaseAbs} gAddr={gAddr} ({gSrc})\n" +
               $"rSize={rSize} rForm={rForm} rTotal={rTotal} rProg={rProg} rBaseAbs={rBaseAbs} rAddr={rAddr} ({rSrc})\n" +
               $"byAddress={byAddress} startAbs={startAbs} gStartIdx={gStartIdx} rStartIdx={rStartIdx}\n" +
               $"gQBase={gQBase} gQAddr={gQAddr} gSkew={gSkew}\n" +
               $"rQBase={rQBase} rQAddr={rQAddr} rSkew={rSkew}\n" +
               $"gAvail={gTotal - gStartIdx} rAvail={rTotal - rStartIdx}";
    }

    /// <summary>
    /// Diagnostic-only: takes the SAME computed gStartIdx as <see cref="DebugAlignment"/>, then
    /// brute-force-tries every golden offset in [-window, +window] sectors around it, counting how
    /// many of the first <paramref name="sampleSectors"/> compared sectors' MAIN channel come out
    /// byte-identical at each candidate offset. Reports the true best-matching offset regardless of
    /// what the address-based computation landed on — the ground-truth check for "is the computed
    /// alignment actually a few sectors off, and by how much."
    /// </summary>
    public static string DebugAlignmentSearch(Stream golden, Stream readback, int window = 32, int sampleSectors = 3000)
    {
        var (gSize, gForm) = RawImageInspector.DetectLayout(golden);
        var (rSize, _) = RawImageInspector.DetectLayout(readback);
        long gTotal = golden.Length / gSize, rTotal = readback.Length / rSize;
        long gProg = gForm is null ? 0 : RawImageInspector.FindLeadInLength(golden, gSize, gForm.Value);

        long gBaseAbs = MainChannelBaseAbs(golden, gSize, gProg, out bool gAddr);
        if (!gAddr) gBaseAbs = ProgramBaseAbs(golden, gSize, gForm, gSize - MainSize, gProg, out gAddr);
        long rBaseAbs = MainChannelBaseAbs(readback, rSize, 0, out bool rAddr);
        var (_, rForm) = RawImageInspector.DetectLayout(readback);
        if (!rAddr) rBaseAbs = ProgramBaseAbs(readback, rSize, rForm, rSize - MainSize, 0, out rAddr);

        long startAbs = (gAddr && rAddr) ? Math.Max(gBaseAbs, rBaseAbs) : 0;
        long gStartIdx0 = gProg + ((gAddr && rAddr) ? startAbs - gBaseAbs : 0);

        int n = (int)Math.Min(sampleSectors, rTotal);
        var gMain = new byte[MainSize];
        var rMain = new byte[MainSize];
        var results = new List<(int offset, int matches)>();
        for (int off = -window; off <= window; off++)
        {
            long gStart = gStartIdx0 + off;
            if (gStart < 0 || gStart + n > gTotal) continue;
            int matches = 0;
            for (int i = 0; i < n; i++)
            {
                golden.Position = (gStart + i) * (long)gSize;
                golden.ReadExactly(gMain, 0, MainSize);
                readback.Position = i * (long)rSize;
                readback.ReadExactly(rMain, 0, MainSize);
                if (gMain.AsSpan().SequenceEqual(rMain)) matches++;
            }
            results.Add((off, matches));
        }
        results.Sort((a, b) => b.matches.CompareTo(a.matches));
        var top = results.Take(5);
        return $"computed gStartIdx={gStartIdx0}, searched offsets [{-window}..{window}] over {n} sectors\n" +
               "top candidates (offset, matching-sector-count):\n" +
               string.Join("\n", top.Select(t => $"  offset {t.offset,+4}: {t.matches}/{n} match" +
                   (t.offset == 0 ? "  <-- what Compare() actually uses" : "")));
    }

    /// <summary>
    /// Diagnostic-only: divides the full overlap between golden and readback into
    /// <paramref name="buckets"/> equal regions and, for each, reports how many sectors have a
    /// CRC-valid Q on BOTH sides and what fraction of those decode to the SAME address at the
    /// CURRENT (uncorrected) alignment — plus, for mismatches, the most common decoded-address
    /// delta in that region. Built to answer one question a single-window search can't: is a
    /// Q-address discrepancy a CONSTANT the sampled window near the track start just missed, or
    /// does it change partway through the track (a materially different, bigger problem)? A flat
    /// mismatch rate with the SAME dominant delta in every region says "constant, just needs a
    /// bigger/differently-placed sample." A rate or dominant delta that changes across regions
    /// says the track's Q behavior itself isn't uniform — no single offset fixes it.
    /// </summary>
    public static string DebugQScan(Stream golden, Stream readback, int buckets = 20)
    {
        var (gSize, gForm) = RawImageInspector.DetectLayout(golden);
        var (rSize, rForm) = RawImageInspector.DetectLayout(readback);
        int gSub = gSize - MainSize, rSub = rSize - MainSize;
        long gTotal = golden.Length / gSize, rTotal = readback.Length / rSize;
        long gProg = gForm is null ? 0 : RawImageInspector.FindLeadInLength(golden, gSize, gForm.Value);
        long rProg = rForm is null ? 0 : RawImageInspector.FindLeadInLength(readback, rSize, rForm.Value);

        long gBaseAbs = MainChannelBaseAbs(golden, gSize, gProg, out bool gAddr);
        if (!gAddr) gBaseAbs = ProgramBaseAbs(golden, gSize, gForm, gSub, gProg, out gAddr);
        long rBaseAbs = MainChannelBaseAbs(readback, rSize, rProg, out bool rAddr);
        if (!rAddr) rBaseAbs = ProgramBaseAbs(readback, rSize, rForm, rSub, rProg, out rAddr);
        long startAbs = (gAddr && rAddr) ? Math.Max(gBaseAbs, rBaseAbs) : 0;
        long gStartIdx = gProg + ((gAddr && rAddr) ? startAbs - gBaseAbs : 0);
        long rStartIdx = rProg + ((gAddr && rAddr) ? startAbs - rBaseAbs : 0);

        long n = Math.Min(gTotal - gStartIdx, rTotal - rStartIdx);
        if (n <= 0 || gSub <= 0 || rSub != gSub || gForm is null || rForm is null)
            return "nothing to scan: no golden/readback overlap, or no comparable sub-channel.";

        var gsBuf = new byte[gSub];
        var rsBuf = new byte[rSub];
        Span<byte> gq = stackalloc byte[12];
        Span<byte> rq = stackalloc byte[12];
        var lines = new List<string>
        {
            $"gStartIdx={gStartIdx} rStartIdx={rStartIdx} overlap={n} sectors — Q match rate across {buckets} regions:"
        };
        long bucketSize = Math.Max(1, n / buckets);
        for (long lo = 0; lo < n; lo += bucketSize)
        {
            long hi = Math.Min(n, lo + bucketSize);
            int considered = 0, matches = 0;
            var deltaVotes = new Dictionary<long, int>();
            for (long i = lo; i < hi; i++)
            {
                ReadSub(golden, gSize, gStartIdx + i, gsBuf, gSub);
                ReadSub(readback, rSize, rStartIdx + i, rsBuf, rSub);
                ExtractQForm(gsBuf, gForm.Value, gq);
                ExtractQForm(rsBuf, rForm.Value, rq);
                if (!RawSubchannel.QCrcValid(gq) || !RawSubchannel.QCrcValid(rq)) continue;
                considered++;
                if (SameAddress(gq, rq)) matches++;
                else
                {
                    long delta = AbsFromQ(rq) - AbsFromQ(gq);
                    deltaVotes[delta] = deltaVotes.GetValueOrDefault(delta) + 1;
                }
            }
            string topDeltas = deltaVotes.Count > 0
                ? string.Join(", ", deltaVotes.OrderByDescending(kv => kv.Value).Take(3)
                    .Select(kv => $"{kv.Key:+#;-#;0}×{kv.Value}"))
                : "-";
            double pct = considered > 0 ? 100.0 * matches / considered : 0;
            lines.Add($"  sectors {lo,8}-{hi - 1,8}: {matches,5}/{considered,5} match ({pct,5:0.0}%)  " +
                      $"top mismatch delta(s): {topDeltas}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>Compare a golden generated image against a raw read-back capture.
    /// When <paramref name="partial"/> is true the read-back is treated as an intentional
    /// SUB-RANGE of the golden (e.g. one track of a multi-track disc read on its own): golden
    /// sectors beyond the read-back's end are reported as an informational note, not as
    /// dropouts, so a per-track verify isn't failed by the sectors it deliberately didn't read.
    /// Interior corruption, mis-addressing and protection loss are still judged normally.</summary>
    public static Report Compare(Stream golden, Stream readback, bool partial = false)
    {
        var (gSize, gForm) = RawImageInspector.DetectLayout(golden);
        var (rSize, rForm) = RawImageInspector.DetectLayout(readback);
        int gSub = gSize - MainSize, rSub = rSize - MainSize;
        long gTotal = golden.Length / gSize, rTotal = readback.Length / rSize;
        var notes = new List<string>();

        // Program-area start (skip the drive-owned lead-in) in each image.
        long gProg = gForm is null ? 0 : RawImageInspector.FindLeadInLength(golden, gSize, gForm.Value);
        long rProg = rForm is null ? 0 : RawImageInspector.FindLeadInLength(readback, rSize, rForm.Value);

        // Absolute address at each program start, so we align by disc address
        // rather than by file offset. Prefer the MAIN-CHANNEL sector header
        // (MM:SS:FF at bytes 12..14) over the Q sub-channel: some real drives
        // report Q at a small, constant sector skew relative to the main-channel
        // data it's bundled with in a raw capture (a documented main/sub
        // read-back misalignment quirk — see MainChannelBaseAbs), and aligning
        // on a skewed Q address then compares every sector against its neighbour
        // instead of itself, turning a byte-perfect burn into "everything
        // differs". The main-channel header has no such cross-channel skew.
        // Falls back to Q (audio tracks have no header at all) or plain index
        // alignment when neither source yields an address.
        long gBaseAbs = MainChannelBaseAbs(golden, gSize, gProg, out bool gAddr);
        bool gQFallback = false;
        if (!gAddr) { gBaseAbs = ProgramBaseAbs(golden, gSize, gForm, gSub, gProg, out gAddr); gQFallback = gAddr; }
        long rBaseAbs = MainChannelBaseAbs(readback, rSize, rProg, out bool rAddr);
        bool rQFallback = false;
        if (!rAddr) { rBaseAbs = ProgramBaseAbs(readback, rSize, rForm, rSub, rProg, out rAddr); rQFallback = rAddr; }
        bool byAddress = gAddr && rAddr;
        if (!byAddress)
            notes.Add("One capture has no readable sub-channel address; aligned by program offset instead.");

        long startAbs = byAddress ? Math.Max(gBaseAbs, rBaseAbs) : 0;
        long gStartIdx = gProg + (byAddress ? startAbs - gBaseAbs : 0);
        long rStartIdx = rProg + (byAddress ? startAbs - rBaseAbs : 0);

        // Guarded local-content refinement: real hardware has shown a Q sub-channel
        // addressing quirk where, on a track with no main-channel header to anchor on
        // (an audio track — ProgramBaseAbs/Q supplies the base address instead), the
        // decoded absolute address lands a small, constant number of sectors short of
        // the disc's true address (confirmed on real captures: a clean, deterministic
        // +2-sector residual, see docs/NEXT.md 2026-08-29). That is a read-path/TOC
        // addressing artifact, not a burn defect, but left uncorrected it turns a
        // byte-perfect track into a false "everything mismatches" verdict. When either
        // side used the Q fallback, probe a small window of candidate offsets by actual
        // main-channel content match and snap onto the true one — but ONLY when the
        // evidence is overwhelming and unambiguous (near-100% match at exactly one
        // offset, near-zero at every other), so a genuinely defective burn is never
        // silently "corrected" into a false pass.
        // Compensating adjustments applied to a fallback side's OWN decoded Q absolute
        // value when checking per-sector Q address equality below. Measured independently,
        // further down (after gSkew/rSkew) — NOT derived from whether the main-channel
        // search just below found anything to fix. First draft tied the two together
        // (only set an adjustment when this search also shifted gStartIdx) and that was a
        // real bug: real hardware showed a track whose base-address vote (ProgramBaseAbs,
        // a small early-exit-prone window) coincidentally locked onto a minority of
        // correctly-decoded frames near the track start, so this main-channel search
        // correctly found no index shift was needed — yet the dominant Q delta across the
        // REST of that same track (99.6% of it) was still short by the same constant every
        // other track needed compensating for. See docs/NEXT.md 2026-08-29.
        long qAdjustG = 0, qAdjustR = 0;
        if (byAddress && (gQFallback ^ rQFallback))   // exactly one side used the Q fallback
        {
            const int window = 16;
            int sample = (int)Math.Min(1500, Math.Min(gTotal - gStartIdx, rTotal - rStartIdx));
            if (sample >= 200)
            {
                var probeG = new byte[MainSize];
                var probeR = new byte[MainSize];
                int bestOffset = 0, bestMatches = -1, secondBest = 0;
                for (int off = -window; off <= window; off++)
                {
                    long gs = gStartIdx + off;
                    if (gs < 0 || gs + sample > gTotal) continue;
                    int matches = 0;
                    for (int i = 0; i < sample; i++)
                    {
                        ReadMain(golden, gSize, gs + i, probeG);
                        ReadMain(readback, rSize, rStartIdx + i, probeR);
                        if (probeG.AsSpan().SequenceEqual(probeR)) matches++;
                    }
                    if (matches > bestMatches) { secondBest = bestMatches < 0 ? 0 : bestMatches; bestOffset = off; bestMatches = matches; }
                    else if (matches > secondBest) secondBest = matches;
                }
                if (bestOffset != 0 && bestMatches >= sample * 0.95 && secondBest <= sample * 0.10)
                {
                    notes.Add($"Alignment auto-corrected by {bestOffset:+#;-#;0} sector(s): the address-based " +
                              "alignment (Q sub-channel, no main-channel header available) landed a few sectors " +
                              $"short of a content-verified exact match ({bestMatches}/{sample} sectors byte-identical " +
                              $"at the corrected offset, vs. at most {secondBest} at any other offset tried) — " +
                              "a known real-drive Q addressing quirk, not a burn defect. See docs/NEXT.md 2026-08-29.");
                    gStartIdx += bestOffset;
                }
            }
        }

        // Q sub-channel vs main-channel skew: some real drives extract the P-W
        // sub-channel a small, constant number of sectors out of step with the
        // main-channel data it's bundled with in the same raw capture (the same
        // quirk MainChannelBaseAbs's doc comment describes — verified on real
        // hardware: main-channel header a rock-solid 150, Q consistently 151 on
        // the exact same capture). That's a read-PATH artifact, not a burn
        // defect, and it would otherwise show up as every single sector's Q
        // "mis-addressed". Measure each file's own Q-vs-main offset independently
        // via the Q-based ProgramBaseAbs, and read that file's sub-channel
        // `skew` sectors away from its main-channel position so both are
        // compared address-for-address rather than file-position-for-position.
        long gQBase = ProgramBaseAbs(golden, gSize, gForm, gSub, gProg, out bool gQAddr);
        long rQBase = ProgramBaseAbs(readback, rSize, rForm, rSub, rProg, out bool rQAddr);
        long gSkew = gQAddr ? gQBase - gBaseAbs : 0;
        long rSkew = rQAddr ? rQBase - rBaseAbs : 0;
        if (gSkew != 0)
            notes.Add($"Golden's sub-channel is offset {gSkew:+#;-#;0} sector(s) from its main channel; " +
                      "corrected before comparing (not a burn defect).");
        if (rSkew != 0)
            notes.Add($"The read-back's sub-channel is offset {rSkew:+#;-#;0} sector(s) from its main " +
                      "channel — a drive read-path quirk, not a burn defect; corrected before comparing.");

        // Independent, decoupled measurement of the per-sector Q address delta for whichever
        // side used the Q fallback. Deliberately NOT inferred from whether the main-channel
        // search above found anything to fix (see the comment where qAdjustG/qAdjustR are
        // declared for why that coupling was wrong) — instead measured directly, at scale,
        // strided evenly across the WHOLE available range (not just a prefix) so a small
        // early cluster of coincidentally-correct frames can't dominate a small sample the
        // way it fooled ProgramBaseAbs's own small early-exit-prone vote. Only acts when one
        // single delta clearly dominates (≥60% of a decently-sized, CRC-valid-on-both-sides
        // sample) — high enough to catch a track that's "only" 92-100% affected (real
        // hardware), low enough to still refuse on a genuinely mixed/defective track.
        if (byAddress && (gQFallback ^ rQFallback) && gSub > 0 && rSub == gSub && gForm is not null && rForm is not null)
        {
            long avail = Math.Min(gTotal - gStartIdx, rTotal - rStartIdx);
            int censusN = (int)Math.Min(5000, avail);
            if (censusN >= 200)
            {
                long stride = Math.Max(1, avail / censusN);
                var gsBuf = new byte[gSub];
                var rsBuf = new byte[rSub];
                Span<byte> gqq = stackalloc byte[12];
                Span<byte> rqq = stackalloc byte[12];
                var votes = new Dictionary<long, int>();
                int considered = 0;
                for (long k = 0; k < censusN; k++)
                {
                    long i = k * stride;
                    if (i >= avail) break;
                    long gsi = gStartIdx + i - gSkew;
                    long rsi = rStartIdx + i - rSkew;
                    if (gsi < 0 || gsi >= gTotal || rsi < 0 || rsi >= rTotal) continue;
                    ReadSub(golden, gSize, gsi, gsBuf, gSub);
                    ReadSub(readback, rSize, rsi, rsBuf, rSub);
                    ExtractQForm(gsBuf, gForm.Value, gqq);
                    ExtractQForm(rsBuf, rForm.Value, rqq);
                    if (!RawSubchannel.QCrcValid(gqq) || !RawSubchannel.QCrcValid(rqq)) continue;
                    considered++;
                    long delta = AbsFromQ(rqq) - AbsFromQ(gqq);
                    votes[delta] = votes.GetValueOrDefault(delta) + 1;
                }
                if (considered >= 150 && votes.Count > 0)
                {
                    var best = votes.OrderByDescending(kv => kv.Value).First();
                    if (best.Key != 0 && best.Value >= considered * 0.60)
                    {
                        long adjust = -best.Key;
                        notes.Add($"Q sub-channel address auto-corrected by {adjust:+#;-#;0} sector(s): " +
                                  $"{best.Value}/{considered} sampled sectors (strided across the whole track) " +
                                  $"consistently decoded {best.Key:+#;-#;0} sector(s) off golden's — a known " +
                                  "real-drive Q addressing quirk, not a burn defect. See docs/NEXT.md 2026-08-29.");
                        if (rQFallback) qAdjustR = adjust; else qAdjustG = -adjust;
                    }
                }
            }
        }

        // NOTE (2026-08-29): a candidate fix for exactly this kind of Q-fallback skew was
        // drafted and then deliberately backed out. It searched a small window of candidate
        // sub-channel read positions the same way the main-channel offset above is
        // recovered — but a synthetic repro built to validate it revealed the mental model
        // was wrong: shifting only the SUB-CHANNEL bytes (leaving main untouched) still
        // moves ProgramBaseAbs's own base-address vote (it reads Q only), so the main-
        // channel search above fires FIRST and "explains" the shift as a value/index issue,
        // never reaching this code path at all. That means this path, as designed, cannot
        // be validated against a controlled repro — only against the real capture, which
        // isn't available in this environment. Track 3's real symptom (main channel already
        // aligned with NO correction needed, per --debug-align-search, yet ~99.6% of the
        // WHOLE track's Q mis-addressed) is still open — see docs/NEXT.md 2026-08-29 for the
        // live investigation and the next diagnostic step (a whole-track, region-by-region
        // Q-match scan, to tell whether the discrepancy is a constant skew this sampling
        // window is simply too small to see, or something that changes partway through the
        // track — a materially different, bigger problem).

        long gAvail = gTotal - gStartIdx, rAvail = rTotal - rStartIdx;
        long compare = Math.Min(gAvail, rAvail);
        if (compare < 0) compare = 0;
        if (rAvail < gAvail)
            notes.Add(partial
                ? $"Partial verify: {gAvail - rAvail:N0} golden sector(s) beyond the read-back were not " +
                  "compared (an intentional sub-range, e.g. a single track); graded on the overlap only."
                : $"The read-back is {gAvail - rAvail:N0} program sector(s) shorter than the golden image " +
                  "(truncated capture or a short burn).");

        long mainMis = 0, edcBroken = 0, subMis = 0, misAddr = 0, protLoss = 0, timing = 0, dropouts = 0;
        long subReadNoise = 0;
        long scrambleNorm = 0;
        var examples = new List<Diff>();
        var perCat = new Dictionary<string, int>();
        void Record(long abs, string cat, Severity sev, string detail)
        {
            if (perCat.GetValueOrDefault(cat) < MaxExamplesPerCategory)
            {
                examples.Add(new Diff(abs, cat, sev, detail));
                perCat[cat] = perCat.GetValueOrDefault(cat) + 1;
            }
        }

        var gMain = new byte[MainSize];
        var rMain = new byte[MainSize];
        var gSubBuf = new byte[Math.Max(1, gSub)];
        var rSubBuf = new byte[Math.Max(1, rSub)];
        Span<byte> gq = stackalloc byte[12];
        Span<byte> rq = stackalloc byte[12];

        for (long i = 0; i < compare; i++)
        {
            long gIdx = gStartIdx + i, rIdx = rStartIdx + i;
            long abs = byAddress ? startAbs + i : i;

            // Main channel always comes from the address-aligned index. Sub-channel
            // is read from that SAME file's own skew-corrected index (see gSkew/
            // rSkew above) — which is usually identical to gIdx/rIdx (skew 0) but
            // isn't when this capture's Q trails or leads its main channel.
            long gSubIdx = gIdx - gSkew, rSubIdx = rIdx - rSkew;
            bool gSubInRange = gSubIdx >= 0 && gSubIdx < gTotal;
            bool rSubInRange = rSubIdx >= 0 && rSubIdx < rTotal;

            ReadMain(golden, gSize, gIdx, gMain);
            ReadMain(readback, rSize, rIdx, rMain);
            if (gSub > 0) { if (gSubInRange) ReadSub(golden, gSize, gSubIdx, gSubBuf, gSub); else Array.Clear(gSubBuf, 0, gSub); }
            if (rSub > 0) { if (rSubInRange) ReadSub(readback, rSize, rSubIdx, rSubBuf, rSub); else Array.Clear(rSubBuf, 0, rSub); }

            // ---- main channel: the exact on-disc 2352 must match -------------
            if (!gMain.AsSpan().SequenceEqual(rMain))
            {
                // Data sectors are STORED scrambled (ECMA-130), but many drives return them
                // DESCRAMBLED on a raw READ CD. Before judging a defect, normalize scramble
                // state: if the two sectors are byte-identical once brought into the same
                // domain, the on-disc content is faithful — only the representation differs
                // (a read-path artifact), so it is not a defect.
                if (ScrambleNormalizedEqual(gMain, rMain))
                {
                    scrambleNorm++;
                    Record(abs, "descrambled-on-read", Severity.Warning,
                        "data sector returned descrambled by the drive; byte-identical to the golden " +
                        "once scramble state is normalized (not a burn defect)");
                }
                else
                {
                    mainMis++;
                    bool broke = DataEdcBroke(gMain, rMain);
                    if (broke) edcBroken++;
                    Record(abs, "main-data", Severity.Defect,
                        broke ? "main-channel bytes differ and the read-back's EDC no longer validates"
                              : "main-channel bytes differ (user data or ECC changed on disc)");
                }
            }

            // ---- sub-channel: byte-exact, then classify any difference -------
            // Skipped when a skew-corrected index fell outside the file — there's
            // no real sub-channel data there to compare (an edge-of-capture
            // artifact of the correction, not a defect to report).
            if (gSub > 0 && rSub == gSub && gSubInRange && rSubInRange &&
                !gSubBuf.AsSpan(0, gSub).SequenceEqual(rSubBuf.AsSpan(0, gSub)))
            {
                subMis++;
                ExtractQForm(gSubBuf, gForm!.Value, gq);
                ExtractQForm(rSubBuf, rForm!.Value, rq);
                bool goldenQValid = RawSubchannel.QCrcValid(gq);

                if (!goldenQValid)
                {
                    // The golden Q was deliberately corrupt — LibCrypt-style
                    // protection. A faithful burn must reproduce it bit-for-bit.
                    protLoss++;
                    Record(abs, "protection-loss", Severity.Defect,
                        "a deliberately-corrupt (protection) Q frame did not survive the burn byte-for-byte");
                }
                else if (!RawSubchannel.QCrcValid(rq))
                {
                    // The golden Q is fine, but the READ-BACK's own Q frame fails its own
                    // CRC — this is a transient sub-channel read error (a bit flipped in
                    // THIS read pass), not evidence the disc holds a wrong address. Real
                    // optical media takes the occasional single-frame Q read glitch; that's
                    // what --reread/--consensus exists to average out. Judging it against
                    // golden byte-for-byte (which "mis-addressed" does) mislabels ordinary
                    // read noise as a burn defect.
                    subReadNoise++;
                    Record(abs, "sub-read-noise", Severity.Warning,
                        "the read-back's own Q frame fails its own CRC (a transient sub-channel " +
                        "read error, not a burn defect) — re-read with --reread/--consensus to confirm");
                }
                else if (!SameAddress(gq, rq, qAdjustG, qAdjustR))
                {
                    misAddr++;
                    Record(abs, "mis-addressed", Severity.Defect,
                        "the read-back Q decodes to a different track/index/address than was written");
                }
                else
                {
                    timing++;
                    Record(abs, "sub-timing", Severity.Warning,
                        "sub-channel ancillary bytes differ but the decoded address is unchanged");
                }
            }
            else if (gSub > 0 && rSub != gSub)
            {
                // Different sub-channel widths: can't byte-compare; note once.
                if (subMis == 0) notes.Add(
                    $"Sub-channel widths differ (golden {gSub}, read-back {rSub}); sub-channel not byte-compared.");
            }
        }

        // Program sectors the read-back never reached count as dropouts — unless this is an
        // intentional partial (sub-range) verify, where the un-read tail is expected, not a defect.
        dropouts = partial ? 0 : Math.Max(0, gAvail - rAvail);

        if (scrambleNorm > 0)
            notes.Add($"{scrambleNorm:N0} data sector(s) came back descrambled from the drive's raw READ CD " +
                      "(data is stored scrambled on disc); compared in the unscrambled domain and found " +
                      "byte-identical — a read-path representation difference, not a burn defect.");

        // A read-back that overlaps the golden in ZERO sectors (empty capture, a read that
        // failed immediately, or a non-overlapping range) is never a pass — there is nothing
        // to have proven. Guard it explicitly so a failed read can't read as success.
        if (compare == 0)
            notes.Add("No overlapping program sectors were compared — the read-back is empty or does " +
                      "not overlap the golden. This is a failed/empty read-back, not a passing burn.");

        if (subReadNoise > 0)
            notes.Add($"{subReadNoise:N0} sub-channel frame(s) failed their OWN CRC on read-back " +
                      "(a transient read error on this pass, not an addressing defect) — re-read with " +
                      "--reread/--consensus to confirm whether they're stable.");

        long defects = mainMis + misAddr + protLoss + dropouts;
        Grade grade = compare == 0 ? Grade.Fail
                    : defects > 0 ? Grade.Fail
                    : (timing > 0 || scrambleNorm > 0 || subReadNoise > 0) ? Grade.PassWithNotes
                    : Grade.Pass;

        return new Report
        {
            Result = grade,
            SectorsCompared = compare,
            MainMismatches = mainMis,
            EdcBroken = edcBroken,
            ScrambleNormalized = scrambleNorm,
            SubMismatches = subMis,
            MisAddressed = misAddr,
            ProtectionLosses = protLoss,
            SubTimingOnly = timing,
            SubReadNoise = subReadNoise,
            Dropouts = dropouts,
            Examples = examples,
            Notes = notes,
        };
    }

    // ---- helpers -----------------------------------------------------------

    private static void ReadSector(Stream s, int size, long idx, byte[] main, byte[] sub, int subSize)
    {
        s.Position = idx * size;
        s.ReadExactly(main, 0, MainSize);
        if (subSize > 0) s.ReadExactly(sub, 0, subSize);
    }

    /// <summary>Read just a sector's main-channel 2352 bytes.</summary>
    private static void ReadMain(Stream s, int size, long idx, byte[] main)
    {
        s.Position = idx * size;
        s.ReadExactly(main, 0, MainSize);
    }

    /// <summary>Read just a sector's sub-channel bytes, independent of where its
    /// main channel was read from — see the skew correction in <see cref="Compare"/>.</summary>
    private static void ReadSub(Stream s, int size, long idx, byte[] sub, int subSize)
    {
        s.Position = idx * size + MainSize;
        s.ReadExactly(sub, 0, subSize);
    }

    /// <summary>Absolute sector address at the program start, from the readable
    /// position Q frames near it; <paramref name="haveAddress"/> is false when
    /// there is no sub-channel to read.
    ///
    /// A single sampled Q frame is not trustworthy on its own: a real drive can
    /// mis-decode one random sector's Q per pass (the same jitter --reread/
    /// --consensus exists to out-vote on a read-back). Anchoring the WHOLE
    /// comparison's alignment on one such frame turns a one-sector Q glitch into
    /// a false "everything mismatches" verdict, because every sector after it is
    /// then compared one sector off. So this samples every valid position frame
    /// in the window and returns the MODE (most-agreed-upon base) rather than
    /// the first hit — one outlier frame no longer skews the alignment.</summary>
    private static long ProgramBaseAbs(Stream s, int size, RawSubcodeForm? form, int subSize,
                                       long progStart, out bool haveAddress)
    {
        haveAddress = false;
        if (form is null || subSize <= 0) return 0;
        var main = new byte[MainSize];
        var sub = new byte[subSize];
        Span<byte> q = stackalloc byte[12];
        long total = s.Length / size;
        var votes = new Dictionary<long, int>();
        for (long idx = progStart; idx < Math.Min(progStart + 400, total); idx++)
        {
            ReadSector(s, size, idx, main, sub, subSize);
            ExtractQForm(sub, form.Value, q);
            if (!RawSubchannel.QCrcValid(q)) continue;
            if ((q[0] & 0x0F) != 1 || q[1] == 0x00) continue;    // want a program position frame
            long abs = AbsFromQ(q) - (idx - progStart);
            votes[abs] = votes.GetValueOrDefault(abs) + 1;
            // Once one candidate has a clear lead (≥3 votes and at least double the
            // runner-up) it's safe to stop early rather than scanning the whole window.
            if (votes[abs] >= 3 && votes[abs] >= 2 * votes.Where(kv => kv.Key != abs).Select(kv => kv.Value).DefaultIfEmpty(0).Max())
                break;
        }
        if (votes.Count == 0) return 0;
        haveAddress = true;
        return votes.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
    }

    /// <summary>Absolute sector address at the program start, derived from the
    /// MAIN-CHANNEL sector header (MM:SS:FF in BCD at bytes 12..14, mode at 15)
    /// rather than the Q sub-channel. Data sectors only (audio has no header —
    /// <paramref name="haveAddress"/> comes back false there, and the caller
    /// falls back to <see cref="ProgramBaseAbs"/>). The header may be in either
    /// scramble state depending on where the capture came from, so both are
    /// tried and whichever decodes to a plausible MSF (seconds &lt; 60, frames
    /// &lt; 75, mode 1 or 2) wins; implausible/ambiguous sectors are skipped.
    /// Votes the mode across the window, same rationale as
    /// <see cref="ProgramBaseAbs"/> — one bad decode shouldn't skew it.</summary>
    private static long MainChannelBaseAbs(Stream s, int size, long progStart, out bool haveAddress)
    {
        haveAddress = false;
        var main = new byte[MainSize];
        var flipped = new byte[MainSize];
        long total = s.Length / size;
        var votes = new Dictionary<long, int>();
        for (long idx = progStart; idx < Math.Min(progStart + 400, total); idx++)
        {
            s.Position = idx * size;
            s.ReadExactly(main, 0, MainSize);
            if (!HasSync(main)) continue;

            if (TryDecodeHeaderAbs(main, out long abs1))
            {
                long b = abs1 - (idx - progStart);
                votes[b] = votes.GetValueOrDefault(b) + 1;
            }
            else
            {
                Array.Copy(main, flipped, MainSize);
                CdScrambler.ScrambleInPlace(flipped);
                if (TryDecodeHeaderAbs(flipped, out long abs2))
                {
                    long b = abs2 - (idx - progStart);
                    votes[b] = votes.GetValueOrDefault(b) + 1;
                }
            }

            if (votes.Count > 0)
            {
                var top = votes.OrderByDescending(kv => kv.Value).First();
                if (top.Value >= 3 && top.Value >= 2 * votes.Where(kv => kv.Key != top.Key)
                        .Select(kv => kv.Value).DefaultIfEmpty(0).Max())
                    break;
            }
        }
        if (votes.Count == 0) return 0;
        haveAddress = true;
        return votes.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
    }

    /// <summary>Decode a main-channel header's MM:SS:FF (bytes 12..14, BCD) to an
    /// absolute sector count, accepting it only when every field is valid BCD,
    /// the MSF is in a sane range (seconds &lt; 60, frames &lt; 75), and the mode
    /// byte (offset 15) is 1 or 2 — anything else means this candidate (raw or
    /// descrambled) isn't actually a decoded header, just noise.</summary>
    private static bool TryDecodeHeaderAbs(ReadOnlySpan<byte> main, out long abs)
    {
        abs = 0;
        byte mode = main[15];
        if (mode != 1 && mode != 2) return false;
        if (!TryBcd(main[12], out int mm) || !TryBcd(main[13], out int ss) || !TryBcd(main[14], out int ff))
            return false;
        if (ss >= 60 || ff >= 75) return false;
        abs = ((long)mm * 60 + ss) * 75 + ff;
        return true;
    }

    private static bool TryBcd(byte b, out int value)
    {
        int hi = b >> 4, lo = b & 0x0F;
        value = hi * 10 + lo;
        return hi <= 9 && lo <= 9;
    }

    /// <summary>Extract the 12-byte Q frame honouring the physical sub-channel layout.</summary>
    private static void ExtractQForm(ReadOnlySpan<byte> sub, RawSubcodeForm form, Span<byte> q12)
    {
        switch (form)
        {
            case RawSubcodeForm.Interleaved96:
                RawSubchannel.ExtractQ(sub, q12);
                break;
            case RawSubcodeForm.Packed96:
                // De-interleaved layout: channel Q occupies bytes 12..23.
                sub.Slice(12, 12).CopyTo(q12);
                break;
            case RawSubcodeForm.Pq16:
                // Formatted P-Q: the Q frame is the first 12 bytes.
                sub.Slice(0, 12).CopyTo(q12);
                break;
        }
    }

    /// <summary>Absolute sector from a position Q frame (BCD M:S:F at q[7..9]).</summary>
    private static long AbsFromQ(ReadOnlySpan<byte> q)
        => ((long)Bcd.To(q[7]) * 60 + Bcd.To(q[8])) * 75 + Bcd.To(q[9]);

    /// <summary>Two position Q frames address the same place (track, index, absolute time).</summary>
    /// <summary>Two position Q frames address the same place (track, index, absolute time).
    /// <paramref name="aAdjust"/>/<paramref name="rAdjust"/> compensate a known, constant,
    /// content-verified sector offset in one side's own decoded absolute address (see the
    /// Q-fallback refinement in <see cref="Compare"/>) — zero in the overwhelmingly common
    /// case, which keeps this a plain byte compare exactly as before.</summary>
    private static bool SameAddress(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, long aAdjust = 0, long rAdjust = 0)
    {
        bool aPos = (a[0] & 0x0F) == 1, bPos = (b[0] & 0x0F) == 1;
        if (aPos != bPos) return false;
        if (!aPos) return true;                       // both non-position: leave to byte compare
        if (a[1] != b[1] || a[2] != b[2]) return false;   // TNO, INDEX
        if (aAdjust == 0 && rAdjust == 0)
            return a[7] == b[7] && a[8] == b[8] && a[9] == b[9];    // absolute M:S:F, byte-exact
        return AbsFromQ(a) + aAdjust == AbsFromQ(b) + rAdjust;
    }

    /// <summary>True when two 2352 main-channel sectors are byte-identical once scramble
    /// state is normalized — i.e. one is the scrambled form of the other. Data sectors are
    /// stored scrambled on disc; some drives return them descrambled on a raw read, so the
    /// raw bytes differ while the on-disc content is faithful. Only meaningful for data
    /// sectors (both carry a valid 12-byte sync, which the scrambler never touches). Audio,
    /// which is never scrambled, can never satisfy this.</summary>
    private static bool ScrambleNormalizedEqual(ReadOnlySpan<byte> golden, ReadOnlySpan<byte> readback)
    {
        if (!HasSync(golden) || !HasSync(readback)) return false;   // both must be data sectors
        Span<byte> flipped = stackalloc byte[MainSize];
        golden.CopyTo(flipped);
        CdScrambler.ScrambleInPlace(flipped);                       // toggle golden's scramble state
        return flipped.SequenceEqual(readback);
    }

    /// <summary>True when the golden sector was a valid data sector whose EDC no
    /// longer validates in the read-back (a real corruption, not a re-encode).</summary>
    private static bool DataEdcBroke(ReadOnlySpan<byte> golden, ReadOnlySpan<byte> readback)
    {
        if (!HasSync(golden)) return false;                       // audio: no EDC to break
        Span<byte> g = stackalloc byte[MainSize];
        Span<byte> r = stackalloc byte[MainSize];
        golden.CopyTo(g); readback.CopyTo(r);
        CdScrambler.ScrambleInPlace(g);                           // de-scramble (self-inverse)
        CdScrambler.ScrambleInPlace(r);
        var (gEdc, _) = VerifyByMode(g);
        if (!gEdc) return false;                                  // golden wasn't clean anyway
        var (rEdc, _) = VerifyByMode(r);
        return !rEdc;
    }

    private static (bool edc, bool ecc) VerifyByMode(ReadOnlySpan<byte> descrambled)
    {
        int mode = descrambled[15];
        if (mode == 1) return EdcEcc.VerifyMode1(descrambled);
        if (mode == 2)
        {
            byte submode = descrambled[18];
            if ((submode & 0x20) == 0) return EdcEcc.VerifyMode2Form1(descrambled);
        }
        return (true, true);                                      // formless Mode 2 / no EDC
    }

    private static bool HasSync(ReadOnlySpan<byte> main)
    {
        if (main[0] != 0x00 || main[11] != 0x00) return false;
        for (int i = 1; i <= 10; i++) if (main[i] != 0xFF) return false;
        return true;
    }
}
