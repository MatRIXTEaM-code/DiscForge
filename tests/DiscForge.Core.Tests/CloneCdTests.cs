// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Convert;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for reading CloneCD images: parsing the .ccd control file and pulling a
/// track's raw sectors out of the accompanying .img / .sub sidecars. The .ccd is
/// written by hand so the layout under test is explicit — two tracks at LBA 0 and
/// 10, lead-out at LBA 25.
/// </summary>
public class CloneCdTests
{
    // Data track 1 at LBA 0 (10 sectors), audio track 2 at LBA 10 (15 sectors),
    // lead-out at LBA 25.
    private const string Ccd =
        "[CloneCD]\r\nVersion=3\r\n\r\n" +
        "[Disc]\r\nTocEntries=5\r\nSessions=1\r\n\r\n" +
        "[Session 1]\r\nPreGapMode=2\r\nPreGapSubC=0\r\n\r\n" +
        "[Entry 0]\r\nSession=1\r\nPoint=0xa0\r\nADR=0x01\r\nControl=0x04\r\nPMin=1\r\nPSec=0\r\nPFrame=0\r\nPLBA=0\r\n\r\n" +
        "[Entry 1]\r\nSession=1\r\nPoint=0xa1\r\nADR=0x01\r\nControl=0x04\r\nPMin=2\r\nPSec=0\r\nPFrame=0\r\nPLBA=0\r\n\r\n" +
        "[Entry 2]\r\nSession=1\r\nPoint=0xa2\r\nADR=0x01\r\nControl=0x04\r\nPMin=0\r\nPSec=0\r\nPFrame=0\r\nPLBA=25\r\n\r\n" +
        "[Entry 3]\r\nSession=1\r\nPoint=0x01\r\nADR=0x01\r\nControl=0x04\r\nPMin=0\r\nPSec=0\r\nPFrame=0\r\nPLBA=0\r\n\r\n" +
        "[Entry 4]\r\nSession=1\r\nPoint=0x02\r\nADR=0x01\r\nControl=0x00\r\nPMin=0\r\nPSec=0\r\nPFrame=0\r\nPLBA=10\r\n\r\n";

    private static byte[] BuildImg(int sectors, int sectorBytes)
    {
        // Every byte of the sector at LBA k is (byte)k, so extracted data is
        // trivially identifiable by which track it came from.
        var img = new byte[sectors * sectorBytes];
        for (int lba = 0; lba < sectors; lba++)
            for (int i = 0; i < sectorBytes; i++)
                img[lba * sectorBytes + i] = (byte)lba;
        return img;
    }

    // ---- parsing -------------------------------------------------------------

    [Fact]
    public void Parses_sessions_entries_and_tracks()
    {
        var toc = CloneCdReader.Parse(Ccd);

        Assert.Equal(3, toc.Version);
        Assert.Equal(1, toc.SessionCount);
        Assert.Equal(5, toc.Entries.Count);
        Assert.Equal(2, toc.Tracks.Count);

        Assert.Equal(1, toc.FirstTrack);
        Assert.Equal(2, toc.LastTrack);
        Assert.Equal(25, toc.LeadOutLba);

        Assert.Equal(0, toc.Tracks[0].StartLba);
        Assert.True(toc.Tracks[0].IsData);
        Assert.Equal(10, toc.Tracks[1].StartLba);
        Assert.False(toc.Tracks[1].IsData);   // audio
    }

    [Fact]
    public void Track_sector_count_spans_to_next_track_or_lead_out()
    {
        var toc = CloneCdReader.Parse(Ccd);
        Assert.Equal(10, CloneCdReader.TrackSectorCount(toc, toc.Tracks[0]));
        Assert.Equal(15, CloneCdReader.TrackSectorCount(toc, toc.Tracks[1]));
    }

    [Fact]
    public void Not_a_ccd_is_rejected()
    {
        Assert.Throws<CloneCdReader.CcdFormatException>(() => CloneCdReader.Parse("just some text"));
    }

    // ---- .img extraction -----------------------------------------------------

    [Fact]
    public void Extracts_a_tracks_raw_sectors_from_the_img()
    {
        var toc = CloneCdReader.Parse(Ccd);
        var img = BuildImg(25, CloneCdReader.ImgSectorBytes);

        using var src = new MemoryStream(img);
        using var track2 = new MemoryStream();
        long n = CloneCdReader.ExtractTrack(toc, toc.Tracks[1], src, track2);

        Assert.Equal(15L * CloneCdReader.ImgSectorBytes, n);
        var bytes = track2.ToArray();
        // Track 2 starts at LBA 10; its first sector is all 0x0A, last is 0x18 (24).
        Assert.Equal(10, bytes[0]);
        Assert.Equal(24, bytes[^1]);
        // And it equals the slice of the .img it was cut from.
        var expected = img.AsSpan(10 * CloneCdReader.ImgSectorBytes,
                                  15 * CloneCdReader.ImgSectorBytes).ToArray();
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void Extracts_the_first_track_from_lba_zero()
    {
        var toc = CloneCdReader.Parse(Ccd);
        var img = BuildImg(25, CloneCdReader.ImgSectorBytes);

        using var src = new MemoryStream(img);
        using var track1 = new MemoryStream();
        long n = CloneCdReader.ExtractTrack(toc, toc.Tracks[0], src, track1);

        Assert.Equal(10L * CloneCdReader.ImgSectorBytes, n);
        Assert.Equal(0, track1.ToArray()[0]);
    }

    [Fact]
    public void A_short_img_is_rejected_not_misread()
    {
        var toc = CloneCdReader.Parse(Ccd);
        // Only 5 sectors, but the TOC needs 25.
        var img = BuildImg(5, CloneCdReader.ImgSectorBytes);

        using var src = new MemoryStream(img);
        using var dst = new MemoryStream();
        Assert.Throws<CloneCdReader.CcdFormatException>(
            () => CloneCdReader.ExtractTrack(toc, toc.Tracks[1], src, dst));
    }

    // ---- .sub reading --------------------------------------------------------

    [Fact]
    public void Reads_a_tracks_raw_subchannel_from_the_sub()
    {
        var toc = CloneCdReader.Parse(Ccd);
        var sub = BuildImg(25, CloneCdReader.SubSectorBytes);

        using var src = new MemoryStream(sub);
        using var dst = new MemoryStream();
        long n = CloneCdReader.ReadSubchannel(toc, toc.Tracks[1], src, dst);

        Assert.Equal(15L * CloneCdReader.SubSectorBytes, n);
        var expected = sub.AsSpan(10 * CloneCdReader.SubSectorBytes,
                                  15 * CloneCdReader.SubSectorBytes).ToArray();
        Assert.Equal(expected, dst.ToArray());
    }

    // ---- via the DiscConverter hub (what BurnView now uses to burn a .ccd) ---

    /// <summary>
    /// BurnView.OpenCdi (App, WinForms — not build-verifiable in the sandbox this project's
    /// sessions run in) converts a picked .ccd to a BIN/CUE via <see cref="DiscConverter"/>
    /// before handing it to the existing CUE burn path, rather than teaching the burn engines a
    /// new format. This is the Core-level part of that: it doesn't touch BurnView at all, but it
    /// does exercise the exact same DiscConverter.Read(".ccd")-then-Write(".cue") round trip
    /// BurnView calls, for real, in a project this session CAN build and run — the strongest
    /// verification available for that logic.
    /// </summary>
    [Fact]
    public void Converts_a_ccd_image_to_bin_cue_via_the_hub()
    {
        using var dir = new TempDir();
        var ccdPath = Path.Combine(dir.Path, "disc.ccd");
        var imgPath = Path.Combine(dir.Path, "disc.img");
        File.WriteAllText(ccdPath, Ccd);
        File.WriteAllBytes(imgPath, BuildImg(25, CloneCdReader.ImgSectorBytes));

        var cuePath = Path.Combine(dir.Path, "disc.cue");
        DiscConverter.Convert(ccdPath, cuePath);

        Assert.True(File.Exists(cuePath));
        var model = DiscConverter.Read(cuePath);
        Assert.Equal(2, model.Tracks.Count);
        Assert.Equal(10, model.Tracks[0].SectorCount);   // data track, LBA 0..9
        Assert.Equal(15, model.Tracks[1].SectorCount);   // audio track, LBA 10..24
        // Same identifiable-by-LBA content the .img was built with (see BuildImg), confirming
        // the round trip didn't shuffle or truncate sector data.
        Assert.Equal(0, model.Tracks[0].Data[0]);
        Assert.Equal(10, model.Tracks[1].Data[0]);
    }

    /// <summary>A private temp directory that deletes itself on Dispose — local to this test
    /// file rather than reusing DiscModel's own private TempDir helper.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "dforge_ccd_hub_test_" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
