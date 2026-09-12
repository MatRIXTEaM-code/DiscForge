using DiscForge.Core.Cue;
using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// A <c>--partial</c> verify of a track deep inside a multi-track disc (track 4+ of an 8-track
/// PS1-style layout) was never exercised before `dforge prove` read every track back on real
/// hardware for the first time (2026-08-28) — every prior <see cref="RawReadbackCompare"/> test
/// covered only a track at or near the start of the golden image. When `prove` failed on tracks
/// 4-8 of a real burn on a previously-untested drive (Lite-On DVDRW SHW-160P6S), this reproduces
/// that exact shape — a deep audio track, byte-perfect, verified with <c>partial: true</c> — in
/// software, to separate "the comparator's alignment logic is broken for this case" (it is not:
/// this passes clean) from "this specific drive's real capture has a quirk this test can't see
/// without the actual bytes" (the open question — see docs/NEXT.md 2026-08-28).
/// </summary>
public class DeepMultiTrackVerifyTests
{
    private static byte[] GoldenMultiTrack(RawSubcodeForm form, out int[] trackStartRel, out int[] trackLenRel)
    {
        // 1 data track + 5 audio tracks, mirroring the shape of ps1-redump.cue (data track,
        // then several minutes of audio tracks one after another).
        int dataSectors = 750;                 // 10s of MODE1 data
        int[] audioSectorsPer = { 900, 1200, 1500, 1800, 2100 }; // tracks 2..6, growing
        int pregap = 150;

        long totalAudio = 0; foreach (var a in audioSectorsPer) totalAudio += a;
        var user = new byte[dataSectors * 2048];
        new System.Random(7).NextBytes(user);
        var pcm = new byte[(totalAudio + 5 * pregap) * 2352]; // room for each track's own pregap+body
        new System.Random(99).NextBytes(pcm);

        var dataBin = new System.IO.MemoryStream(user);
        var audioBin = new System.IO.MemoryStream(pcm);

        // Build MSF timestamps by hand, one FILE per source (matches real ps1-redump-style cues
        // where the data track and audio tracks are commonly SEPARATE physical files as far as the
        // cue's own FILE statements are concerned isn't required here — DiscLayout only needs each
        // track's own stream via the resolver).
        string Msf(int sectors) => $"{sectors / 4500:D2}:{sectors % 4500 / 75:D2}:{sectors % 75:D2}";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("FILE \"d.bin\" BINARY");
        sb.AppendLine("  TRACK 01 MODE1/2048");
        sb.AppendLine("    INDEX 01 00:00:00");

        int rel = 0; // relative sector position within the audio bin
        int cursor = dataSectors; // absolute LBA cursor (informational only)
        trackStartRel = new int[audioSectorsPer.Length];
        trackLenRel = new int[audioSectorsPer.Length];
        for (int i = 0; i < audioSectorsPer.Length; i++)
        {
            sb.AppendLine("FILE \"a.bin\" BINARY");
            sb.AppendLine($"  TRACK {(i + 2):D2} AUDIO");
            if (i == 0)
            {
                sb.AppendLine($"    INDEX 00 {Msf(rel)}");
                rel += pregap;
                sb.AppendLine($"    INDEX 01 {Msf(rel)}");
            }
            else
            {
                sb.AppendLine($"    INDEX 01 {Msf(rel)}");
            }
            trackStartRel[i] = rel;
            rel += audioSectorsPer[i];
            trackLenRel[i] = rel - trackStartRel[i];
        }

        var cue = sb.ToString();
        var layout = DiscLayout.FromCue(CueSheet.Parse(cue), name => name == "d.bin" ? dataBin : audioBin);
        var img = new System.IO.MemoryStream();
        RawImageGenerator.Generate(layout, form, img);
        return img.ToArray();
    }

    [Theory]
    [InlineData(RawSubcodeForm.Interleaved96)]
    [InlineData(RawSubcodeForm.Packed96)]
    public void A_byte_perfect_capture_of_a_deep_audio_track_still_passes(RawSubcodeForm form)
    {
        var golden = GoldenMultiTrack(form, out _, out _);
        int sectorSize = form == RawSubcodeForm.Interleaved96 || form == RawSubcodeForm.Packed96 ? 2448 : 2368;

        // Locate track 5 (index 3 of 5 audio tracks, i.e. the disc's 6th track overall) the same
        // way `prove` would: scan the golden's own Q sub-channel for the track-number change.
        long total = golden.LongLength / sectorSize;
        int wantTrack = 5; // TNO of the 4th audio track (data=1, audio=2..6) — has a track after it
        long trackStart = -1, trackEnd = -1;
        var q = new byte[12];
        for (long s = 0; s < total; s++)
        {
            var sub = golden.AsSpan((int)(s * sectorSize + 2352), sectorSize - 2352);
            SubcodeFrame.ExtractQ(sub, form, q);
            if (!RawSubchannel.QCrcValid(q)) continue;
            if ((q[0] & 0x0F) != 1) continue;
            if (q[1] == wantTrack && q[2] != 0 && trackStart == -1) trackStart = s;
            if (q[1] == wantTrack + 1 && trackEnd == -1 && trackStart != -1) { trackEnd = s; break; }
        }
        Assert.True(trackStart > 0, "didn't find track start in synthetic golden");
        Assert.True(trackEnd > trackStart, "didn't find track end in synthetic golden");

        var readback = golden.AsSpan((int)(trackStart * sectorSize), (int)((trackEnd - trackStart) * sectorSize)).ToArray();

        var report = RawReadbackCompare.Compare(new System.IO.MemoryStream(golden), new System.IO.MemoryStream(readback), partial: true);

        Assert.True(report.Result != RawReadbackCompare.Grade.Fail,
            $"byte-perfect deep-track capture should never FAIL: {report.Summary}\n" +
            string.Join("\n", report.Examples.Take(5).Select(e => $"  {e.Category}: {e.Detail}")));
    }

    /// <summary>
    /// Reproduces the real-hardware bug found 2026-08-29: burning and reading back an 8-track
    /// PS1 disc on a Lite-On DVDRW SHW-160P6S, tracks 4-8 (deep audio tracks, no main-channel
    /// header to anchor alignment on) all FAILED verify despite the burn itself being correct —
    /// <c>--debug-align-search</c> on the kept captures showed the true, content-verified
    /// alignment was always exactly 2 sectors after the one <see cref="RawReadbackCompare"/>'s
    /// address-based alignment computed (100% match at +2, 0% everywhere else in a ±32-sector
    /// search), because that drive's decoded Q absolute address reads 2 sectors short of the
    /// disc's true address on a track with no header to fall back to. This builds that exact
    /// shape directly: main-channel content is byte-perfect, but the Q sub-channel's own
    /// absolute-address field is deliberately encoded 2 sectors low on every sector of the
    /// capture (track/index/relative-time all left correct and CRC-valid — only the field
    /// <see cref="RawReadbackCompare"/> reads to compute the base address is wrong), and asserts
    /// the fix (a guarded, content-verified auto-correction — see Compare()) still passes it,
    /// with a note explaining the correction rather than a silent pass.
    /// </summary>
    [Theory]
    [InlineData(RawSubcodeForm.Interleaved96)]
    [InlineData(RawSubcodeForm.Packed96)]
    public void A_readback_whose_Q_address_reads_a_few_sectors_short_still_passes_via_auto_correction(RawSubcodeForm form)
    {
        var golden = GoldenMultiTrack(form, out _, out _);
        int sectorSize = 2448;

        long total = golden.LongLength / sectorSize;
        int wantTrack = 5;
        long trackStart = -1, trackEnd = -1;
        var q = new byte[12];
        for (long s = 0; s < total; s++)
        {
            var sub = golden.AsSpan((int)(s * sectorSize + 2352), sectorSize - 2352);
            SubcodeFrame.ExtractQ(sub, form, q);
            if (!RawSubchannel.QCrcValid(q)) continue;
            if ((q[0] & 0x0F) != 1) continue;
            if (q[1] == wantTrack && q[2] != 0 && trackStart == -1) trackStart = s;
            if (q[1] == wantTrack + 1 && trackEnd == -1 && trackStart != -1) { trackEnd = s; break; }
        }
        Assert.True(trackStart > 0, "didn't find track start in synthetic golden");
        Assert.True(trackEnd > trackStart, "didn't find track end in synthetic golden");

        var readback = golden.AsSpan((int)(trackStart * sectorSize), (int)((trackEnd - trackStart) * sectorSize)).ToArray();

        // Corrupt every sector's Q absolute-address field by -2 sectors, leaving the main
        // channel, and everything else about Q (track, index, relative time, CRC), correct.
        const int shortBy = 2;
        long n = (trackEnd - trackStart);
        var origQ = new byte[12];
        var rw = new byte[96];
        for (long i = 0; i < n; i++)
        {
            var sub = readback.AsSpan((int)(i * sectorSize + 2352), sectorSize - 2352);
            SubcodeFrame.ExtractQ(sub, form, origQ);
            if (!RawSubchannel.QCrcValid(origQ)) continue;      // leave read-noise sectors alone
            if ((origQ[0] & 0x0F) != 1) continue;                // only touch position frames

            var control = (QControl)(origQ[0] >> 4);
            int track = Bcd.To(origQ[1]);
            int index = Bcd.To(origQ[2]);
            var relative = new Msf(Bcd.To(origQ[3]), Bcd.To(origQ[4]), Bcd.To(origQ[5]));
            long trueAbs = ((long)Bcd.To(origQ[7]) * 60 + Bcd.To(origQ[8])) * 75 + Bcd.To(origQ[9]);
            var wrongAbsolute = Msf.FromSectors(Math.Max(0, trueAbs - shortBy));

            var badQ = SubQ.Position(control, track, index, relative, wrongAbsolute);

            SubcodeFrame.ExtractRw(sub, form, rw);
            bool p = form == RawSubcodeForm.Interleaved96 && (sub[0] & 0x80) != 0;
            var frame = new SubcodeFrame { P = p, Q = badQ, Rw = rw };
            switch (form)
            {
                case RawSubcodeForm.Interleaved96: frame.EmitInterleaved96(sub); break;
                case RawSubcodeForm.Packed96: frame.EmitPacked96(sub); break;
            }
        }

        var report = RawReadbackCompare.Compare(new System.IO.MemoryStream(golden), new System.IO.MemoryStream(readback), partial: true);

        Assert.True(report.Result != RawReadbackCompare.Grade.Fail,
            $"a byte-perfect capture with a short-by-{shortBy} Q address should auto-correct, not FAIL: " +
            $"{report.Summary}\n" + string.Join("\n", report.Examples.Take(5).Select(e => $"  {e.Category}: {e.Detail}")));
        Assert.Contains(report.Notes, note => note.Contains("auto-corrected"));
    }

    /// <summary>
    /// Reproduces the OTHER real-hardware failure shape found 2026-08-29, on the same disc's track
    /// 3: unlike tracks 4-8 (Q short by 2 essentially everywhere, caught by the test above), track
    /// 3's main channel aligned perfectly with NO index correction needed
    /// (<c>--debug-align-search</c> found 100% match already at offset 0) — yet
    /// <c>--debug-q-scan</c> showed 99.6% of the WHOLE track still decoded its Q 2 sectors short.
    /// Root cause: <c>ProgramBaseAbs</c>'s own base-address vote only samples the first 400 sectors
    /// and exits early once one candidate reaches a 3-vote, 2x-margin lead — track 3 apparently had
    /// just enough correctly-decoded frames clustered right at its start to win that early vote
    /// before the dominant short-by-2 pattern (the rest of the track) ever got counted, so the
    /// index computed from it happened to be right, and the first fix (main-channel-content-search-
    /// triggered Q compensation) never fired because it was wrongly gated on that same search having
    /// found something to correct. This builds that exact trap deterministically: the first 5
    /// position-frame sectors of the capture decode with a CORRECT (delta 0) Q address — enough to
    /// win ProgramBaseAbs's early-exit vote outright — while every sector after that is short by 2,
    /// matching the shape of the other test. Confirms the fix's decoupling: the Q-address census
    /// must fire on its own evidence, not on whether a main-channel shift also happened to fire.
    /// </summary>
    [Theory]
    [InlineData(RawSubcodeForm.Interleaved96)]
    [InlineData(RawSubcodeForm.Packed96)]
    public void A_readback_whose_early_frames_win_a_misleading_vote_still_auto_corrects(RawSubcodeForm form)
    {
        var golden = GoldenMultiTrack(form, out _, out _);
        int sectorSize = 2448;

        long total = golden.LongLength / sectorSize;
        int wantTrack = 5;
        long trackStart = -1, trackEnd = -1;
        var q = new byte[12];
        for (long s = 0; s < total; s++)
        {
            var sub = golden.AsSpan((int)(s * sectorSize + 2352), sectorSize - 2352);
            SubcodeFrame.ExtractQ(sub, form, q);
            if (!RawSubchannel.QCrcValid(q)) continue;
            if ((q[0] & 0x0F) != 1) continue;
            if (q[1] == wantTrack && q[2] != 0 && trackStart == -1) trackStart = s;
            if (q[1] == wantTrack + 1 && trackEnd == -1 && trackStart != -1) { trackEnd = s; break; }
        }
        Assert.True(trackStart > 0, "didn't find track start in synthetic golden");
        Assert.True(trackEnd > trackStart, "didn't find track end in synthetic golden");

        var readback = golden.AsSpan((int)(trackStart * sectorSize), (int)((trackEnd - trackStart) * sectorSize)).ToArray();

        const int shortBy = 2;
        const int correctPrefixSectors = 5;   // enough to win ProgramBaseAbs's 3-vote/2x-margin early exit
        long n = trackEnd - trackStart;
        var origQ = new byte[12];
        var rw = new byte[96];
        long touched = 0;
        for (long i = 0; i < n; i++)
        {
            var sub = readback.AsSpan((int)(i * sectorSize + 2352), sectorSize - 2352);
            SubcodeFrame.ExtractQ(sub, form, origQ);
            if (!RawSubchannel.QCrcValid(origQ)) continue;
            if ((origQ[0] & 0x0F) != 1) continue;
            if (touched < correctPrefixSectors) { touched++; continue; }   // leave these correct
            touched++;

            var control = (QControl)(origQ[0] >> 4);
            int track = Bcd.To(origQ[1]);
            int index = Bcd.To(origQ[2]);
            var relative = new Msf(Bcd.To(origQ[3]), Bcd.To(origQ[4]), Bcd.To(origQ[5]));
            long trueAbs = ((long)Bcd.To(origQ[7]) * 60 + Bcd.To(origQ[8])) * 75 + Bcd.To(origQ[9]);
            var wrongAbsolute = Msf.FromSectors(Math.Max(0, trueAbs - shortBy));

            var badQ = SubQ.Position(control, track, index, relative, wrongAbsolute);

            SubcodeFrame.ExtractRw(sub, form, rw);
            bool p = form == RawSubcodeForm.Interleaved96 && (sub[0] & 0x80) != 0;
            var frame = new SubcodeFrame { P = p, Q = badQ, Rw = rw };
            switch (form)
            {
                case RawSubcodeForm.Interleaved96: frame.EmitInterleaved96(sub); break;
                case RawSubcodeForm.Packed96: frame.EmitPacked96(sub); break;
            }
        }

        var report = RawReadbackCompare.Compare(new System.IO.MemoryStream(golden), new System.IO.MemoryStream(readback), partial: true);

        long misAddrCount = report.Examples.Count(e => e.Category == "mis-addressed");
        Assert.True(misAddrCount <= 5,
            $"a track whose early Q frames win a misleading base-address vote should still auto-correct " +
            $"via the independent census, not stay badly mis-addressed: {report.Summary}\n" +
            string.Join("\n", report.Examples.Take(5).Select(e => $"  {e.Category}: {e.Detail}")));
        Assert.Contains(report.Notes, note => note.Contains("Q sub-channel address auto-corrected"));
    }
}
