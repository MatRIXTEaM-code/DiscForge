// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;
using DiscForge.Core.Dumping;
using DiscForge.Core.Raw;
using DiscForge.Core.Util;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The post-extraction audit examined against the exact failure it was built
/// for: the half-void dump, where a drive muted 135,417 data sectors to zeros
/// with SUCCESS status and nothing ever re-read the file to notice. The audit
/// trusts only the bytes on disk — these tests hand it files that lie in the
/// same way and check that it refuses to be lied to.
/// </summary>
public class ExtractionAuditTests
{
    private static byte[] Mode1Sector(long lba)
    {
        var user = new byte[2048];
        new Random((int)lba).NextBytes(user);
        var raw = new byte[2352];
        RawSectorBuilder.BuildMode1(user, Msf.FromSectors(lba + 150), raw);
        return raw;
    }

    private static MemoryStream Image(params byte[][] sectors)
    {
        var ms = new MemoryStream();
        foreach (var s in sectors) ms.Write(s, 0, s.Length);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void CleanDataSpan_Passes_WithFullCensus()
    {
        var sectors = Enumerable.Range(0, 20).Select(i => Mode1Sector(i)).ToArray();
        using var img = Image(sectors);
        var r = ExtractionAudit.Run(img,
            new[] { new ExtractionAudit.SpanSpec("track 1 (data)", 0, 20, false, false) },
            edcSampleTarget: 0);

        Assert.True(r.Passed);
        Assert.Equal("PASS", r.Grade);
        var s = Assert.Single(r.Spans);
        Assert.Equal(0, s.SyncMissing);
        Assert.Equal(0, s.AllZero);
        Assert.Equal(20, s.EdcChecked);
        Assert.Equal(0, s.EdcErrors);
    }

    [Fact]
    public void HalfVoidDataSpan_Fails_TheDumpThatStartedThis()
    {
        // 10 real sectors, then 10 muted to zero — the drive's lie, on disk.
        var sectors = Enumerable.Range(0, 10).Select(i => Mode1Sector(i))
            .Concat(Enumerable.Range(0, 10).Select(_ => new byte[2352]))
            .ToArray();
        using var img = Image(sectors);
        var r = ExtractionAudit.Run(img,
            new[] { new ExtractionAudit.SpanSpec("track 1 (data)", 0, 20, false, false) });

        Assert.False(r.Passed);
        Assert.Equal("FAIL", r.Grade);
        var s = Assert.Single(r.Spans);
        Assert.Equal(10, s.SyncMissing);
        Assert.Equal(10, s.AllZero);
        Assert.Equal(10, s.LongestZeroRun);
        Assert.Contains(r.Failures, f => f.Contains("no sync pattern"));
    }

    [Fact]
    public void CorruptedEdc_Fails()
    {
        var sectors = Enumerable.Range(0, 8).Select(i => Mode1Sector(i)).ToArray();
        sectors[3][100] ^= 0xFF;                     // damage user data; EDC now wrong
        using var img = Image(sectors);
        var r = ExtractionAudit.Run(img,
            new[] { new ExtractionAudit.SpanSpec("track 1 (data)", 0, 8, false, false) },
            edcSampleTarget: 0);

        Assert.False(r.Passed);
        Assert.Contains(r.Failures, f => f.Contains("fail EDC"));
        Assert.Equal(1, r.Spans[0].EdcErrors);
    }

    [Fact]
    public void AudioSpan_Silence_IsCensusedNeverFailed()
    {
        // Digital silence is legitimate audio; the audit reports it and moves on.
        var silence = Enumerable.Range(0, 15).Select(_ => new byte[2352]).ToArray();
        using var img = Image(silence);
        var r = ExtractionAudit.Run(img,
            new[] { new ExtractionAudit.SpanSpec("track 2 (audio)", 0, 15, true, false) });

        Assert.True(r.Passed);
        var s = Assert.Single(r.Spans);
        Assert.Equal(15, s.AllZero);
        Assert.Equal(15, s.LongestZeroRun);
        Assert.Equal(0, s.SyncMissing);
        Assert.Equal(0, s.EdcChecked);
    }

    [Fact]
    public void BoundarySpan_UnstructuredContent_IsInformationalOnly()
    {
        // A pregap span at a data→audio transition: geometry, not damage.
        var junk = Enumerable.Range(0, 5).Select(i =>
        {
            var b = new byte[2352];
            new Random(i).NextBytes(b);
            return b;
        }).ToArray();
        using var img = Image(junk);
        var r = ExtractionAudit.Run(img,
            new[] { new ExtractionAudit.SpanSpec("track 2 pregap", 0, 5, true, true) });

        Assert.True(r.Passed);
        Assert.Empty(r.Failures);
    }

    [Fact]
    public void MultiSpan_OffsetsAreRespected_AndOneBadSpanFailsTheAudit()
    {
        // data(6 clean) + audio(4) + data(5 with one void) — mixed-mode shape.
        var all = Enumerable.Range(0, 6).Select(i => Mode1Sector(i))
            .Concat(Enumerable.Range(0, 4).Select(_ => new byte[2352]))
            .Concat(Enumerable.Range(6, 5).Select(i => Mode1Sector(i)))
            .ToArray();
        all[12] = new byte[2352];                    // void inside the second data span
        using var img = Image(all);
        var r = ExtractionAudit.Run(img, new[]
        {
            new ExtractionAudit.SpanSpec("track 1 (data)", 0, 6, false, false),
            new ExtractionAudit.SpanSpec("track 2 (audio)", 6, 4, true, false),
            new ExtractionAudit.SpanSpec("track 3 (data)", 10, 5, false, false),
        }, edcSampleTarget: 0);

        Assert.False(r.Passed);
        Assert.Equal(0, r.Spans[0].SyncMissing);
        Assert.Equal(4, r.Spans[1].AllZero);
        Assert.Equal(1, r.Spans[2].SyncMissing);
        var f = Assert.Single(r.Failures);
        Assert.StartsWith("track 3 (data)", f);
    }
}
