// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Cdi;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Round-trip tests for the descriptor mode repair.
///
/// This is the one piece of DiscForge that writes into an existing image file,
/// so the tests are written around what must never happen rather than only what
/// should: track data must be untouched, a correct image must be left alone,
/// and an unrecognised layout must be refused rather than guessed at. A silent
/// regression here would corrupt archives whose discs may no longer exist.
/// </summary>
public class CdiModeRepairTests
{
    /// <summary>Build a raw 2352-byte sector carrying a valid sync pattern and
    /// the given mode byte, so the classifier has something real to read.</summary>
    private static byte[] RawSector(byte mode, byte fill)
    {
        var s = new byte[2352];
        s[0] = 0x00;
        for (int i = 1; i <= 10; i++) s[i] = 0xFF;
        s[11] = 0x00;
        s[12] = 0x00; s[13] = 0x02; s[14] = 0x00;   // MSF
        s[15] = mode;
        for (int i = 16; i < s.Length; i++) s[i] = (byte)((i * 7 + fill) & 0xFF);
        return s;
    }

    private static byte[] TrackData(byte mode, uint sectors, byte fill)
    {
        var d = new byte[sectors * 2352];
        for (uint i = 0; i < sectors; i++)
            RawSector(mode, (byte)(fill + i)).CopyTo(d, (int)(i * 2352));
        return d;
    }

    /// <summary>
    /// Write a CDI whose descriptor declares one mode but whose sectors actually
    /// carry another. That mismatch is precisely what the repair exists to fix.
    /// </summary>
    private static MemoryStream BuildImage(CdiTrackMode declared, byte actualModeByte,
                                           uint sectors = 16, int trackCount = 1)
    {
        var tracks = new List<CdiWriter.TrackInput>();
        for (int i = 0; i < trackCount; i++)
            tracks.Add(new CdiWriter.TrackInput
            {
                Mode = declared,
                SectorSize = CdiSectorSize.S2352,
                PregapSectors = 0,
                LengthSectors = sectors,
                StartLba = (uint)(i * sectors),
                Filename = $"TRACK{i + 1:D2}.BIN",
                Data = TrackData(actualModeByte, sectors, (byte)(i * 3)),
            });

        var ms = new MemoryStream();
        CdiWriter.Write(ms, CdiVersion.V35,
            new[] { (IReadOnlyList<CdiWriter.TrackInput>)tracks });
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Sector_classifier_reads_the_mode_from_the_header()
    {
        Assert.Equal(CdiTrackMode.Mode1, CdiModeRepair.ClassifySector(RawSector(1, 0)));
        Assert.Equal(CdiTrackMode.Mode2, CdiModeRepair.ClassifySector(RawSector(2, 0)));
    }

    [Fact]
    public void Sector_classifier_requires_a_sync_pattern()
    {
        // Without sync, byte 15 is just data — an audio sector would otherwise be
        // "classified" from whatever PCM happened to sit there.
        var noSync = RawSector(1, 0);
        noSync[3] = 0x00;
        Assert.Null(CdiModeRepair.ClassifySector(noSync));
    }

    [Fact]
    public void Sector_classifier_has_no_opinion_on_mode_zero()
    {
        Assert.Null(CdiModeRepair.ClassifySector(RawSector(0, 0)));
    }

    [Fact]
    public void Analysis_finds_a_mode_1_declaration_over_mode_2_sectors()
    {
        using var img = BuildImage(CdiTrackMode.Mode1, actualModeByte: 2);

        var report = CdiModeRepair.Analyse(img);

        Assert.True(report.DescriptorLayoutVerified);
        Assert.Equal(1, report.RepairsNeeded);
        var f = Assert.Single(report.Findings);
        Assert.Equal(CdiTrackMode.Mode1, f.Declared);
        Assert.Equal(CdiTrackMode.Mode2, f.Actual);
        Assert.True(f.NeedsRepair);
    }

    [Fact]
    public void Analysis_leaves_a_correct_image_alone()
    {
        using var img = BuildImage(CdiTrackMode.Mode2, actualModeByte: 2);

        var report = CdiModeRepair.Analyse(img);

        Assert.True(report.DescriptorLayoutVerified);
        Assert.False(report.AnyRepairNeeded);
    }

    [Fact]
    public void Repair_corrects_the_descriptor_and_the_parser_agrees()
    {
        using var img = BuildImage(CdiTrackMode.Mode1, actualModeByte: 2);

        int patched = CdiModeRepair.Repair(img, out var report);

        Assert.Equal(1, patched);

        // The decisive check: re-parse and confirm the file now says Mode 2.
        img.Position = 0;
        var reparsed = CdiParser.Parse(img);
        Assert.Equal(CdiTrackMode.Mode2, reparsed.AllTracks.First().Mode);
    }

    [Fact]
    public void Repair_does_not_touch_track_data()
    {
        using var img = BuildImage(CdiTrackMode.Mode1, actualModeByte: 2, sectors: 8);

        img.Position = 0;
        var before = img.ToArray();
        long dataLength = 8L * 2352;

        CdiModeRepair.Repair(img, out _);

        var after = img.ToArray();

        // Every byte of the track data region must be identical. Only four bytes
        // per track, inside the descriptor, may differ.
        Assert.Equal(before.Length, after.Length);
        for (long i = 0; i < dataLength; i++)
            Assert.Equal(before[i], after[i]);

        int differing = 0;
        for (long i = 0; i < before.Length; i++)
            if (before[i] != after[i]) differing++;
        Assert.Equal(1, differing);      // Mode1(1) -> Mode2(2): one byte changes
    }

    [Fact]
    public void Repair_handles_several_tracks()
    {
        using var img = BuildImage(CdiTrackMode.Mode1, actualModeByte: 2,
                                   sectors: 8, trackCount: 3);

        int patched = CdiModeRepair.Repair(img, out var report);

        Assert.Equal(3, patched);
        Assert.Equal(3, report.Findings.Count);

        img.Position = 0;
        var reparsed = CdiParser.Parse(img);
        Assert.All(reparsed.AllTracks, t => Assert.Equal(CdiTrackMode.Mode2, t.Mode));
    }

    [Fact]
    public void Repair_is_idempotent()
    {
        using var img = BuildImage(CdiTrackMode.Mode1, actualModeByte: 2);

        CdiModeRepair.Repair(img, out _);
        var afterFirst = img.ToArray();

        int second = CdiModeRepair.Repair(img, out var report);

        Assert.Equal(0, second);
        Assert.False(report.AnyRepairNeeded);
        Assert.Equal(afterFirst, img.ToArray());
    }

    [Fact]
    public void A_correct_image_is_written_to_not_at_all()
    {
        using var img = BuildImage(CdiTrackMode.Mode2, actualModeByte: 2);
        var before = img.ToArray();

        int patched = CdiModeRepair.Repair(img, out _);

        Assert.Equal(0, patched);
        Assert.Equal(before, img.ToArray());
    }

    [Fact]
    public void Cooked_tracks_are_left_alone()
    {
        // A 2048-byte track has had its header stripped; there is no evidence in
        // the stored bytes, so the declared mode must stand rather than being
        // guessed from data that happens to look like a sync pattern.
        var track = new CdiWriter.TrackInput
        {
            Mode = CdiTrackMode.Mode1,
            SectorSize = CdiSectorSize.S2048,
            PregapSectors = 0,
            LengthSectors = 8,
            StartLba = 0,
            Data = new byte[8 * 2048],
        };
        using var ms = new MemoryStream();
        CdiWriter.Write(ms, CdiVersion.V35,
            new[] { (IReadOnlyList<CdiWriter.TrackInput>)new[] { track } });
        ms.Position = 0;

        var report = CdiModeRepair.Analyse(ms);

        var f = Assert.Single(report.Findings);
        Assert.Null(f.Actual);
        Assert.False(f.NeedsRepair);
    }

    [Fact]
    public void Audio_tracks_are_left_alone()
    {
        var track = new CdiWriter.TrackInput
        {
            Mode = CdiTrackMode.Audio,
            SectorSize = CdiSectorSize.S2352,
            PregapSectors = 0,
            LengthSectors = 8,
            StartLba = 0,
            Data = new byte[8 * 2352],
        };
        using var ms = new MemoryStream();
        CdiWriter.Write(ms, CdiVersion.V35,
            new[] { (IReadOnlyList<CdiWriter.TrackInput>)new[] { track } });
        ms.Position = 0;

        var report = CdiModeRepair.Analyse(ms);

        var f = Assert.Single(report.Findings);
        Assert.Null(f.Actual);
        Assert.False(f.NeedsRepair);
    }

    [Fact]
    public void Repair_refuses_a_stream_it_cannot_write_to()
    {
        using var img = BuildImage(CdiTrackMode.Mode1, actualModeByte: 2);
        var readOnly = new MemoryStream(img.ToArray(), writable: false);

        Assert.Throws<ArgumentException>(() => CdiModeRepair.Repair(readOnly, out _));
    }

    [Fact]
    public void Analysis_reports_the_mode_field_offset_within_the_descriptor()
    {
        using var img = BuildImage(CdiTrackMode.Mode1, actualModeByte: 2, sectors: 8);

        var report = CdiModeRepair.Analyse(img);
        var f = Assert.Single(report.Findings);

        // The offset must land inside the descriptor, i.e. after the track data.
        Assert.True(f.ModeFieldOffset > 8L * 2352);
        Assert.True(f.ModeFieldOffset < img.Length);
    }
}