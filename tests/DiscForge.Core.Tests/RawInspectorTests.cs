// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;
using DiscForge.Core.Raw;
using DiscForge.Core.Util;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The inspector examined against images the generator produced — and against
/// deliberately damaged ones. The inspector shares no intent with the
/// generator (format detection by CRC voting, CD-TEXT from pack bytes, ECC by
/// syndromes), so agreement here is evidence, and disagreement on real
/// hardware rips will be findings.
/// </summary>
public class RawInspectorTests
{
    private const string AlbumCue = """
        CATALOG 1234567890123
        TITLE "Round Trip"
        FILE "x.bin" BINARY
          TRACK 01 AUDIO
            TITLE "A"
            ISRC GBAYE0500001
            INDEX 01 00:00:00
          TRACK 02 AUDIO
            INDEX 00 00:01:55
            INDEX 01 00:01:59
        """;

    private static MemoryStream GenerateAlbum(RawSubcodeForm form)
    {
        var pcm = new byte[(130 + 4 + 8) * 2352];
        new Random(11).NextBytes(pcm);
        using var bin = new MemoryStream(pcm);
        using var layout = DiscLayout.FromCue(CueSheet.Parse(AlbumCue), _ => bin);
        var img = new MemoryStream();
        RawImageGenerator.Generate(layout, form, img);
        img.Position = 0;
        return img;
    }

    [Theory]
    [InlineData(RawSubcodeForm.Pq16, 2368)]
    [InlineData(RawSubcodeForm.Packed96, 2448)]
    public void Inspect_DetectsFormatByQCrcVoting(RawSubcodeForm form, int size)
    {
        using var img = GenerateAlbum(form);
        var r = RawImageInspector.Inspect(img);
        Assert.Equal(form, r.Form);
        Assert.Equal(size, r.SectorSize);
    }

    [Fact]
    public void Inspect_DecodesTocMcnIsrcAndCdText()
    {
        using var img = GenerateAlbum(RawSubcodeForm.Packed96);
        var r = RawImageInspector.Inspect(img);

        Assert.True(r.HasLeadIn);
        Assert.Equal(RawImageGenerator.LeadInSectors, r.LeadInSectors);
        Assert.Equal("1234567890123", r.Mcn);
        Assert.Equal("Round Trip", r.AlbumTitle);
        Assert.Equal(2, r.Tracks.Count);
        Assert.Equal(150, r.Tracks[0].StartSector);
        Assert.Equal(150 + 134, r.Tracks[1].StartSector);
        Assert.Equal("GBAYE0500001", r.Tracks[0].Isrc);
        Assert.Equal(0, r.QCrcErrors);
    }

    [Fact]
    public void Inspect_DataDisc_CleanThenCorruptedIsCaught()
    {
        var user = new byte[40 * 2048];
        new Random(2).NextBytes(user);
        using var bin = new MemoryStream(user);
        const string cue = "FILE \"d.bin\" BINARY\n  TRACK 01 MODE1/2048\n    INDEX 01 00:00:00\n";
        using var layout = DiscLayout.FromCue(CueSheet.Parse(cue), _ => bin);
        var img = new MemoryStream();
        RawImageGenerator.Generate(layout, RawSubcodeForm.Packed96, img);

        img.Position = 0;
        var clean = RawImageInspector.Inspect(img, deep: true);
        Assert.True(clean.Tracks[0].Scrambled);
        Assert.Equal(1, clean.Tracks[0].Mode);
        Assert.Equal(40, clean.Tracks[0].DataSectorsChecked);
        Assert.Equal(0, clean.Tracks[0].EdcErrors + clean.Tracks[0].EccErrors);

        img.GetBuffer()[(RawImageGenerator.LeadInSectors + 150 + 20) * 2448L + 300] ^= 0xFF;
        img.Position = 0;
        var bad = RawImageInspector.Inspect(img, deep: true);
        Assert.Equal(1, bad.Tracks[0].EdcErrors);
        Assert.Equal(1, bad.Tracks[0].EccErrors);
    }

    [Fact]
    public void Inspect_BareMainChannelBin_VerifiesEcc()
    {
        // The path that gold-checks the ECC conventions against real rips.
        var img = new MemoryStream();
        var sector = new byte[2352];
        var user = new byte[2048];
        var rnd = new Random(6);
        for (int s = 0; s < 30; s++)
        {
            rnd.NextBytes(user);
            RawSectorBuilder.BuildMode1(user, Msf.FromSectors(150 + s), sector);
            img.Write(sector, 0, 2352);
        }
        img.Position = 0;
        var r = RawImageInspector.Inspect(img, deep: true);

        Assert.Null(r.Form);
        Assert.Equal(2352, r.SectorSize);
        Assert.False(r.Tracks[0].Scrambled);
        Assert.Equal(30, r.Tracks[0].DataSectorsChecked);
        Assert.Equal(0, r.Tracks[0].EdcErrors + r.Tracks[0].EccErrors);
    }

    /// <summary>
    /// The honesty fix. A bare 2352 image that is half real Mode 1 data and
    /// half zeros — the exact shape of the half-void dump — used to inspect
    /// as "clean" because sync-less sectors were silently skipped. They must
    /// be counted, reported, and carried in the Report.
    /// </summary>
    [Fact]
    public void Inspect_HalfVoidMainChannelBin_CountsSynclessSectors()
    {
        var img = new MemoryStream();
        var sector = new byte[2352];
        var user = new byte[2048];
        var rnd = new Random(7);
        for (int s = 0; s < 15; s++)
        {
            rnd.NextBytes(user);
            RawSectorBuilder.BuildMode1(user, Msf.FromSectors(150 + s), sector);
            img.Write(sector, 0, 2352);
        }
        img.Write(new byte[15 * 2352]);              // the drive's lie: muted zeros
        img.Position = 0;

        var r = RawImageInspector.Inspect(img, deep: true);

        Assert.Equal(30, r.MainSectorsSampled);
        Assert.Equal(15, r.MainSynclessSectors);
        Assert.Equal(15, r.MainZeroSectors);
        Assert.Equal(15, r.Tracks[0].DataSectorsChecked);   // structured half still verified
        Assert.Contains(r.Notes, n => n.Contains("NO sync pattern"));
    }

    /// <summary>
    /// With subcode present the inspector KNOWS a track is data, so a
    /// sync-less sector inside it is damage the EDC/ECC checks could not even
    /// reach — counted per-track and noted, never skipped.
    /// </summary>
    [Fact]
    public void Inspect_DataTrackWithMutedSectors_CountsThemAsSyncless()
    {
        var userData = new byte[40 * 2048];
        new Random(3).NextBytes(userData);
        using var bin = new MemoryStream(userData);
        const string cue = "FILE \"d.bin\" BINARY\n  TRACK 01 MODE1/2048\n    INDEX 01 00:00:00\n";
        using var layout = DiscLayout.FromCue(CueSheet.Parse(cue), _ => bin);
        var img = new MemoryStream();
        RawImageGenerator.Generate(layout, RawSubcodeForm.Packed96, img);

        // Mute three data sectors' main channel; their subcode stays intact.
        var buf = img.GetBuffer();
        foreach (int s in new[] { 10, 11, 12 })
            Array.Clear(buf, (int)((RawImageGenerator.LeadInSectors + 150 + s) * 2448L), 2352);

        img.Position = 0;
        var r = RawImageInspector.Inspect(img, deep: true);

        Assert.Equal(3, r.Tracks[0].SynclessSectors);
        Assert.Equal(37, r.Tracks[0].DataSectorsChecked);
        Assert.Equal(0, r.Tracks[0].EdcErrors + r.Tracks[0].EccErrors);
        Assert.Contains(r.Notes, n => n.Contains("track 1") && n.Contains("NO sync pattern"));
    }
}

/// <summary>CD+G: program-area R–W passthrough from .sub sidecars.</summary>
public class CdgTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;
    /// <summary>
    /// Temp-directory teardown. Deliberately tolerant: a file still held open
    /// by a stream the runtime hasn't collected yet would otherwise fail a test
    /// whose assertions all passed, and an intermittent red build teaches people
    /// to ignore the suite. The directory is under the OS temp path and gets
    /// cleared regardless.
    /// </summary>
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string MakeCdgSet(int sectors, int subLengthOverride = -1)
    {
        var bin = new byte[sectors * 2352];
        new Random(13).NextBytes(bin);
        File.WriteAllBytes(Path.Combine(_dir, "k.bin"), bin);

        int subLen = subLengthOverride >= 0 ? subLengthOverride : sectors * 96;
        var sub = new byte[subLen];
        for (int i = 0; i < subLen; i++)
            sub[i] = (byte)(0xC0 | ((i / 96 + i % 96) & 0x3F));   // P/Q garbage on top
        File.WriteAllBytes(Path.Combine(_dir, "k.sub"), sub);

        var cuePath = Path.Combine(_dir, "k.cue");
        File.WriteAllText(cuePath,
            "FILE \"k.bin\" BINARY\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00\n");
        return cuePath;
    }

    [Fact]
    public void SubSidecar_SymbolsPassThrough_QStaysOurs()
    {
        var cue = MakeCdgSet(20);
        using var layout = DiscLayout.FromCueFile(cue);
        Assert.True(layout.HasProgramRw);

        using var img = new MemoryStream();
        RawImageGenerator.Generate(layout, RawSubcodeForm.Packed96, img);

        var subBytes = new byte[96];
        img.Position = (RawImageGenerator.LeadInSectors + 150 + 5) * 2448L + 2352;
        img.ReadExactly(subBytes, 0, 96);

        var rw = new byte[96];
        SubcodeFrame.ExtractRw(subBytes, RawSubcodeForm.Packed96, rw);
        for (int i = 0; i < 96; i++)
            Assert.Equal((5 + i) & 0x3F, rw[i]);            // symbols intact

        var q = new byte[12];
        SubcodeFrame.ExtractQ(subBytes, RawSubcodeForm.Packed96, q);
        Assert.Equal(Crc16.ComputeInverted(q.AsSpan(0, 10)),
            (ushort)((q[10] << 8) | q[11]));                 // Q is ours, valid
        Assert.Equal(0x01, q[1]);
    }

    [Fact]
    public void SubSidecar_WrongLength_RefusedAtLoad()
    {
        var cue = MakeCdgSet(20, subLengthOverride: 100);
        var ex = Assert.Throws<InvalidDataException>(() => DiscLayout.FromCueFile(cue));
        Assert.Contains("96 bytes per sector", ex.Message);
    }
}
