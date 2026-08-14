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
                          $"({SubTimingOnly:N0} sub-timing, {ScrambleNormalized:N0} descrambled-on-read) — see notes.",
            _ => $"FAIL — {MainMismatches + MisAddressed + ProtectionLosses + Dropouts:N0} defect(s) across " +
                 $"{SectorsCompared:N0} sectors (main {MainMismatches:N0}, mis-addressed {MisAddressed:N0}, " +
                 $"protection-loss {ProtectionLosses:N0}, dropout {Dropouts:N0}).",
        };
    }

    private const int MainSize = 2352;
    private const int MaxExamplesPerCategory = 8;

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
        // rather than by file offset. Falls back to index-alignment when a
        // capture has no sub-channel to read an address from.
        long gBaseAbs = ProgramBaseAbs(golden, gSize, gForm, gSub, gProg, out bool gAddr);
        long rBaseAbs = ProgramBaseAbs(readback, rSize, rForm, rSub, rProg, out bool rAddr);
        bool byAddress = gAddr && rAddr;
        if (!byAddress)
            notes.Add("One capture has no readable sub-channel address; aligned by program offset instead.");

        long startAbs = byAddress ? Math.Max(gBaseAbs, rBaseAbs) : 0;
        long gStartIdx = gProg + (byAddress ? startAbs - gBaseAbs : 0);
        long rStartIdx = rProg + (byAddress ? startAbs - rBaseAbs : 0);

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

            ReadSector(golden, gSize, gIdx, gMain, gSubBuf, gSub);
            ReadSector(readback, rSize, rIdx, rMain, rSubBuf, rSub);

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
            if (gSub > 0 && rSub == gSub && !gSubBuf.AsSpan(0, gSub).SequenceEqual(rSubBuf.AsSpan(0, gSub)))
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
                else if (!SameAddress(gq, rq))
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

        long defects = mainMis + misAddr + protLoss + dropouts;
        Grade grade = compare == 0 ? Grade.Fail
                    : defects > 0 ? Grade.Fail
                    : (timing > 0 || scrambleNorm > 0) ? Grade.PassWithNotes
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

    /// <summary>Absolute sector address at the program start, from the first
    /// readable position Q; <paramref name="haveAddress"/> is false when there
    /// is no sub-channel to read.</summary>
    private static long ProgramBaseAbs(Stream s, int size, RawSubcodeForm? form, int subSize,
                                       long progStart, out bool haveAddress)
    {
        haveAddress = false;
        if (form is null || subSize <= 0) return 0;
        var main = new byte[MainSize];
        var sub = new byte[subSize];
        Span<byte> q = stackalloc byte[12];
        long total = s.Length / size;
        for (long idx = progStart; idx < Math.Min(progStart + 400, total); idx++)
        {
            ReadSector(s, size, idx, main, sub, subSize);
            ExtractQForm(sub, form.Value, q);
            if (!RawSubchannel.QCrcValid(q)) continue;
            if ((q[0] & 0x0F) != 1 || q[1] == 0x00) continue;    // want a program position frame
            long abs = AbsFromQ(q) - (idx - progStart);
            haveAddress = true;
            return abs;
        }
        return 0;
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
    private static bool SameAddress(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        bool aPos = (a[0] & 0x0F) == 1, bPos = (b[0] & 0x0F) == 1;
        if (aPos != bPos) return false;
        if (!aPos) return true;                       // both non-position: leave to byte compare
        // TNO, INDEX, and absolute M:S:F.
        return a[1] == b[1] && a[2] == b[2] && a[7] == b[7] && a[8] == b[8] && a[9] == b[9];
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
