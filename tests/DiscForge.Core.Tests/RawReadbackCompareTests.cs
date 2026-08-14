// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;
using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The RAW read-back comparator — proving a burn landed on disc byte-for-byte,
/// the verification ImgBurn never had (it MD5s user data and can't touch the
/// sub-channel). Every case starts from a real golden image composed by
/// <see cref="RawImageGenerator"/>, then perturbs a read-back the way a drive
/// or a defect would, and checks the verdict.
/// </summary>
public class RawReadbackCompareTests
{
    private const int SubSize = 96;                 // Packed96
    private static readonly int SectorSize = 2448;

    /// <summary>A small Mode 1 data disc as a golden Packed96 image.</summary>
    private static byte[] GoldenDataDisc(int sectors = 6)
    {
        var user = new byte[sectors * 2048];
        new Random(7).NextBytes(user);
        var bin = new MemoryStream(user);
        string cue = "FILE \"d.bin\" BINARY\n  TRACK 01 MODE1/2048\n    INDEX 01 00:00:00\n";
        var layout = DiscLayout.FromCue(CueSheet.Parse(cue), _ => bin);
        var img = new MemoryStream();
        RawImageGenerator.Generate(layout, RawSubcodeForm.Packed96, img);
        return img.ToArray();
    }

    /// <summary>File offset of the first index-1 data sector (after 150 pregap).</summary>
    private static long DataSectorOffset(int rel = 0)
        => (RawImageGenerator.LeadInSectors + 150 + rel) * (long)SectorSize;

    [Fact]
    public void An_identical_readback_passes_cleanly()
    {
        var golden = GoldenDataDisc();
        var report = RawReadbackCompare.Compare(new MemoryStream(golden), new MemoryStream((byte[])golden.Clone()));

        Assert.Equal(RawReadbackCompare.Grade.Pass, report.Result);
        Assert.Equal(0, report.MainMismatches);
        Assert.Equal(0, report.SubMismatches);
        Assert.True(report.SectorsCompared > 150);      // the whole program area
    }

    [Fact]
    public void A_flipped_user_byte_is_a_main_data_defect_with_broken_edc()
    {
        var golden = GoldenDataDisc();
        var readback = (byte[])golden.Clone();
        readback[DataSectorOffset() + 1000] ^= 0xFF;      // corrupt one on-disc byte

        var report = RawReadbackCompare.Compare(new MemoryStream(golden), new MemoryStream(readback));

        Assert.Equal(RawReadbackCompare.Grade.Fail, report.Result);
        Assert.Equal(1, report.MainMismatches);
        Assert.Equal(1, report.EdcBroken);
        Assert.Contains(report.Examples, d => d.Category == "main-data");
    }

    [Fact]
    public void A_descrambled_data_readback_is_not_a_defect()
    {
        // The burn-day reality (PX-W5224A, rung 6): data sectors are STORED scrambled, but the
        // drive's raw READ CD returns them DESCRAMBLED. The golden is scrambled, so a naive
        // byte-compare flags every data sector — yet the on-disc content is faithful. The
        // comparator must normalize scramble state and grade it PASS, not FAIL.
        var golden = GoldenDataDisc();
        var readback = (byte[])golden.Clone();

        // Descramble every program data sector's main channel (what the drive did on read).
        long progStart = RawImageGenerator.LeadInSectors * (long)SectorSize;
        int descrambled = 0;
        for (long off = progStart; off + 2352 <= readback.Length; off += SectorSize)
        {
            var main = readback.AsSpan((int)off, 2352);
            if (main[0] != 0x00 || main[11] != 0x00) continue;      // sync marks a data sector
            bool sync = true;
            for (int i = 1; i <= 10; i++) if (main[i] != 0xFF) { sync = false; break; }
            if (!sync) continue;
            CdScrambler.ScrambleInPlace(main);                       // scramble⁻¹ = descramble
            descrambled++;
        }
        Assert.True(descrambled > 0, "the golden should contain scrambled data sectors to descramble");

        var report = RawReadbackCompare.Compare(new MemoryStream(golden), new MemoryStream(readback));

        Assert.Equal(RawReadbackCompare.Grade.PassWithNotes, report.Result);
        Assert.Equal(0, report.MainMismatches);                     // NOT a defect
        Assert.Equal(0, report.EdcBroken);
        Assert.Equal(descrambled, report.ScrambleNormalized);       // every one recognised as benign
        Assert.Contains(report.Examples, d => d.Category == "descrambled-on-read");
        Assert.Contains(report.Notes, n => n.Contains("descrambled"));
    }

    [Fact]
    public void An_ancillary_subchannel_byte_is_only_a_timing_warning()
    {
        var golden = GoldenDataDisc();
        var readback = (byte[])golden.Clone();
        // Byte 30 of the sub frame is R–W (past Q at 12..23): address is untouched.
        readback[DataSectorOffset() + 2352 + 30] ^= 0x3F;

        var report = RawReadbackCompare.Compare(new MemoryStream(golden), new MemoryStream(readback));

        Assert.Equal(RawReadbackCompare.Grade.PassWithNotes, report.Result);
        Assert.Equal(0, report.MainMismatches);
        Assert.Equal(1, report.SubTimingOnly);
        Assert.Equal(0, report.MisAddressed);
    }

    [Fact]
    public void A_changed_q_address_is_a_mis_addressed_defect()
    {
        var golden = GoldenDataDisc();
        var readback = (byte[])golden.Clone();
        // Packed Q sits at bytes 12..23; byte 21 is the absolute-frame BCD (q[9]).
        readback[DataSectorOffset(3) + 2352 + 21] ^= 0x01;

        var report = RawReadbackCompare.Compare(new MemoryStream(golden), new MemoryStream(readback));

        Assert.Equal(RawReadbackCompare.Grade.Fail, report.Result);
        Assert.True(report.MisAddressed >= 1);
        Assert.Contains(report.Examples, d => d.Category == "mis-addressed");
    }

    [Fact]
    public void A_corrupt_protection_q_that_does_not_survive_is_a_protection_loss()
    {
        // Model a verbatim (LibCrypt) sector: the GOLDEN carries a deliberately
        // corrupt Q; a faithful burn must reproduce it. Here the read-back has
        // the pristine (valid) Q instead — protection was lost.
        var pristine = GoldenDataDisc();
        var golden = (byte[])pristine.Clone();
        golden[DataSectorOffset(2) + 2352 + 12] ^= 0x08;   // corrupt golden Q's CRC

        var report = RawReadbackCompare.Compare(new MemoryStream(golden), new MemoryStream(pristine));

        Assert.Equal(RawReadbackCompare.Grade.Fail, report.Result);
        Assert.Equal(1, report.ProtectionLosses);
        Assert.Contains(report.Examples, d => d.Category == "protection-loss");
    }

    [Fact]
    public void A_truncated_readback_reports_dropouts()
    {
        var golden = GoldenDataDisc();
        // Drop the last two program sectors from the read-back.
        var readback = golden.AsSpan(0, golden.Length - 2 * SectorSize).ToArray();

        var report = RawReadbackCompare.Compare(new MemoryStream(golden), new MemoryStream(readback));

        Assert.Equal(RawReadbackCompare.Grade.Fail, report.Result);
        Assert.Equal(2, report.Dropouts);
    }

    [Fact]
    public void A_partial_subrange_readback_is_not_failed_by_the_uncovered_tail()
    {
        // Rung 7 (mixed-mode): a single track is read on its own, so the read-back is a
        // deliberate sub-range of the whole-disc golden. Without --partial the un-read tail
        // reads as dropouts → FAIL; with --partial it grades the overlap only.
        var golden = GoldenDataDisc();
        // Read-back covers all but the last 3 program sectors (an intentional sub-range).
        var readback = golden.AsSpan(0, golden.Length - 3 * SectorSize).ToArray();

        var strict = RawReadbackCompare.Compare(new MemoryStream(golden), new MemoryStream(readback));
        Assert.Equal(RawReadbackCompare.Grade.Fail, strict.Result);
        Assert.Equal(3, strict.Dropouts);

        var lenient = RawReadbackCompare.Compare(new MemoryStream(golden), new MemoryStream(readback), partial: true);
        Assert.NotEqual(RawReadbackCompare.Grade.Fail, lenient.Result);
        Assert.Equal(0, lenient.Dropouts);
        Assert.True(lenient.SectorsCompared > 150);
        Assert.Contains(lenient.Notes, n => n.Contains("Partial verify"));
    }

    [Fact]
    public void An_empty_readback_is_a_failure_not_a_pass()
    {
        // A read that failed immediately (0 bytes) must never grade PASS — even with --partial,
        // where the whole golden is "beyond" the read-back. Comparing zero sectors proves nothing.
        var golden = GoldenDataDisc();
        var empty = Array.Empty<byte>();

        foreach (var partial in new[] { false, true })
        {
            var r = RawReadbackCompare.Compare(new MemoryStream(golden), new MemoryStream(empty), partial);
            Assert.Equal(RawReadbackCompare.Grade.Fail, r.Result);
            Assert.Equal(0, r.SectorsCompared);
            Assert.Contains("no sectors were compared", r.Summary, System.StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- burn-day media: audio (PQ-16 is hardware test #1), multi-track,
    //      MCN/ISRC, and the interleaved-96 form -----------------------------

    /// <summary>A 2-track audio disc with MCN + ISRC, in the given subcode form.</summary>
    private static byte[] GoldenAudioDisc(RawSubcodeForm form)
    {
        var pcm = new byte[(130 + 4 + 8) * 2352];
        new Random(11).NextBytes(pcm);
        var bin = new MemoryStream(pcm);
        string cue = """
            CATALOG 1234567890123
            FILE "x.bin" BINARY
              TRACK 01 AUDIO
                ISRC GBAYE0500001
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                INDEX 00 00:01:55
                INDEX 01 00:01:59
            """;
        var layout = DiscLayout.FromCue(CueSheet.Parse(cue), _ => bin);
        var img = new MemoryStream();
        RawImageGenerator.Generate(layout, form, img);
        return img.ToArray();
    }

    [Theory]
    [InlineData(RawSubcodeForm.Pq16)]        // 2368 — hardware test #1 (audio, PQ-16)
    [InlineData(RawSubcodeForm.Packed96)]    // 2448 packed
    public void An_identical_audio_readback_passes_in_every_subcode_form(RawSubcodeForm form)
    {
        var golden = GoldenAudioDisc(form);
        var report = RawReadbackCompare.Compare(
            new MemoryStream(golden), new MemoryStream((byte[])golden.Clone()));

        Assert.Equal(RawReadbackCompare.Grade.Pass, report.Result);
        Assert.Equal(0, report.MainMismatches);
        Assert.Equal(0, report.SubMismatches);
        Assert.True(report.SectorsCompared > 130);
    }

    [Fact]
    public void A_flipped_audio_sample_is_a_main_defect_but_breaks_no_edc()
    {
        int size = 2368;                                   // Pq16
        var golden = GoldenAudioDisc(RawSubcodeForm.Pq16);
        var readback = (byte[])golden.Clone();
        long off = (RawImageGenerator.LeadInSectors + 150 + 2) * (long)size;   // an audio sample
        readback[off + 400] ^= 0xFF;

        var report = RawReadbackCompare.Compare(new MemoryStream(golden), new MemoryStream(readback));

        Assert.Equal(RawReadbackCompare.Grade.Fail, report.Result);
        Assert.Equal(1, report.MainMismatches);
        Assert.Equal(0, report.EdcBroken);                 // audio has no EDC to break
    }

    [Fact]
    public void The_interleaved96_form_round_trips_and_classifies()
    {
        var golden = GoldenDataDisc();
        var img = new MemoryStream();
        // Re-emit the same layout as Interleaved96 to exercise that Q-extraction path.
        var user = new byte[6 * 2048];
        new Random(7).NextBytes(user);
        var bin = new MemoryStream(user);
        string cue = "FILE \"d.bin\" BINARY\n  TRACK 01 MODE1/2048\n    INDEX 01 00:00:00\n";
        var layout = DiscLayout.FromCue(CueSheet.Parse(cue), _ => bin);
        RawImageGenerator.Generate(layout, RawSubcodeForm.Interleaved96, img);
        var golden96 = img.ToArray();

        // identical → PASS
        Assert.Equal(RawReadbackCompare.Grade.Pass,
            RawReadbackCompare.Compare(new MemoryStream(golden96), new MemoryStream((byte[])golden96.Clone())).Result);

        // an R–W ancillary tweak → timing warning (address intact)
        var rb = (byte[])golden96.Clone();
        rb[(RawImageGenerator.LeadInSectors + 150) * 2448L + 2352 + 40] ^= 0x20;
        var r2 = RawReadbackCompare.Compare(new MemoryStream(golden96), new MemoryStream(rb));
        Assert.Equal(RawReadbackCompare.Grade.PassWithNotes, r2.Result);
        Assert.True(r2.SubTimingOnly >= 1);
    }

    [Fact]
    public void A_readback_without_the_lead_in_still_aligns_by_address()
    {
        // A program-only read-back (no drive-owned lead-in) must align by the
        // decoded disc address and still compare clean against the golden.
        var golden = GoldenDataDisc();
        long leadIn = RawImageGenerator.LeadInSectors * (long)SectorSize;
        var readback = golden.AsSpan((int)leadIn).ToArray();

        var report = RawReadbackCompare.Compare(new MemoryStream(golden), new MemoryStream(readback));

        Assert.Equal(RawReadbackCompare.Grade.Pass, report.Result);
        Assert.True(report.SectorsCompared > 150);
    }

    [Fact]
    public void The_certificate_renders_the_verdict_in_json_and_html()
    {
        var golden = GoldenDataDisc();
        var readback = (byte[])golden.Clone();
        readback[DataSectorOffset() + 1000] ^= 0xFF;      // one defect
        var r = RawReadbackCompare.Compare(new MemoryStream(golden), new MemoryStream(readback));

        string json = RawReadbackReport.Json(r, "golden.img", "readback.bin", golden.Length, readback.Length);
        Assert.Contains("\"grade\":\"Fail\"", json);
        Assert.Contains("\"mainMismatches\":1", json);
        Assert.Contains("\"category\":\"main-data\"", json);

        string html = RawReadbackReport.Html(r, "golden.img", "readback.bin",
            golden.Length, readback.Length, utcStamp: "2026-08-10T12:00:00Z");
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("FAIL", html);
        Assert.Contains("main-data", html);
        Assert.Contains("2026-08-10T12:00:00Z", html);
        Assert.DoesNotContain("<script", html);           // self-contained, no scripts
    }
}
