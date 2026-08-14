using System.Buffers.Binary;
using DiscForge.Core.Cdi;
using DiscForge.Core.Devices;
using DiscForge.Core.Mmc;
using DiscForge.Core.Reading;
using Xunit;

namespace DiscForge.Core.Tests;

public class TocAndReadPlannerTests
{
    // --- helpers: build a realistic MMC format-0 TOC response ------------------

    private static byte[] BuildToc(int firstTrack, (int num, byte control, uint lba)[] tracks, uint leadOut)
    {
        var descs = new List<byte>();
        foreach (var (num, control, lba) in tracks)
        {
            descs.Add(0);
            descs.Add((byte)((1 << 4) | (control & 0x0F)));   // ADR=1
            descs.Add((byte)num);
            descs.Add(0);
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, lba);
            descs.AddRange(b);
        }
        // Lead-out descriptor.
        descs.Add(0);
        descs.Add((1 << 4) | 0x04);
        descs.Add(0xAA);
        descs.Add(0);
        var lo = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lo, leadOut);
        descs.AddRange(lo);

        int lastTrack = tracks.Max(t => t.num);
        var body = new List<byte> { (byte)firstTrack, (byte)lastTrack };
        body.AddRange(descs);

        var resp = new byte[2 + body.Count];
        BinaryPrimitives.WriteUInt16BigEndian(resp, (ushort)body.Count);
        body.CopyTo(resp, 2);
        return resp;
    }

    private static DriveCapabilities Drive(bool cdRead = true, bool raw = false) => new()
    {
        DevicePath = @"\\.\D:", Vendor = "TEST", Model = "READER", FirmwareRevision = "1.0",
        CdRead = cdRead, CdWrite = false, DvdRead = true, DvdWrite = false,
        BdRead = false, BdWrite = false, RawDao96 = raw,
    };

    // --- TOC parsing ----------------------------------------------------------

    [Fact]
    public void Parses_single_data_track_and_derives_length_from_leadout()
    {
        var resp = BuildToc(1, new[] { (1, (byte)0x04, 0u) }, leadOut: 333000);
        var toc = TocParser.Parse(resp);

        Assert.Equal(1, toc.FirstTrack);
        Assert.Equal(1, toc.LastTrack);
        Assert.Single(toc.Tracks);
        Assert.True(toc.Tracks[0].IsData);
        Assert.Equal(333000u, toc.Tracks[0].LengthSectors);   // length is derived, not stored
        Assert.False(toc.IsMixedMode);
    }

    [Fact]
    public void Derives_audio_track_lengths_from_neighbours()
    {
        var resp = BuildToc(1, new[]
        {
            (1, (byte)0x00, 0u), (2, (byte)0x00, 20000u), (3, (byte)0x00, 45000u),
        }, leadOut: 70000);

        var toc = TocParser.Parse(resp);

        Assert.Equal(new uint[] { 20000, 25000, 25000 },
                     toc.Tracks.Select(t => t.LengthSectors).ToArray());
        Assert.All(toc.Tracks, t => Assert.True(t.IsAudio));
        Assert.False(toc.HasData);
    }

    [Fact]
    public void Detects_mixed_mode()
    {
        var resp = BuildToc(1, new[]
        {
            (1, (byte)0x04, 0u), (2, (byte)0x00, 30000u),
        }, leadOut: 50000);

        var toc = TocParser.Parse(resp);

        Assert.True(toc.IsMixedMode);
        Assert.True(toc.Tracks[0].IsData);
        Assert.True(toc.Tracks[1].IsAudio);
    }

    [Fact]
    public void Pre_emphasis_and_copy_bits_are_not_mistaken_for_the_data_bit()
    {
        // 0x01 = pre-emphasis, 0x02 = copy permitted. Neither means "data".
        var resp = BuildToc(1, new[]
        {
            (1, (byte)0x01, 0u), (2, (byte)0x02, 10000u),
        }, leadOut: 20000);

        var toc = TocParser.Parse(resp);

        Assert.All(toc.Tracks, t => Assert.True(t.IsAudio));
        Assert.True(toc.Tracks[0].PreEmphasis);
        Assert.True(toc.Tracks[1].CopyPermitted);
    }

    [Fact]
    public void Truncated_toc_is_rejected_not_silently_misread()
    {
        var resp = BuildToc(1, new[] { (1, (byte)0x04, 0u) }, leadOut: 1000);
        var truncated = resp.AsSpan(0, resp.Length - 4).ToArray();

        Assert.Throws<InvalidDataException>(() => TocParser.Parse(truncated));
    }

    [Fact]
    public void Toc_without_leadout_is_rejected()
    {
        // Hand-build a response with no 0xAA descriptor.
        var resp = new byte[2 + 2 + 8];
        BinaryPrimitives.WriteUInt16BigEndian(resp, 10);
        resp[2] = 1; resp[3] = 1;
        resp[4 + 1] = (1 << 4) | 0x04;
        resp[4 + 2] = 1;

        Assert.Throws<InvalidDataException>(() => TocParser.Parse(resp));
    }

    [Fact]
    public void Read10_cdb_is_well_formed()
    {
        // Cooked 2048-byte reads go via READ(10), not READ CD: a real drive
        // rejects READ CD with sector type "Any" + user-data-only fields
        // ("Illegal request: invalid field in CDB") because it cannot infer
        // which bytes to strip.
        var cdb = MmcCommands.Read10(0x00123456, 27);

        Assert.Equal(10, cdb.Length);
        Assert.Equal(0x28, cdb[0]);
        Assert.Equal(0x00123456u, BinaryPrimitives.ReadUInt32BigEndian(cdb.AsSpan(2, 4)));
        Assert.Equal(27, BinaryPrimitives.ReadUInt16BigEndian(cdb.AsSpan(7, 2)));
    }

    [Fact]
    public void ReadCd_cdb_packs_lba_length_and_flags()
    {
        var cdb = MmcCommands.ReadCd(0x00ABCDEF, 27,
            MmcCommands.ExpectedSectorType.Cdda, MmcCommands.SectorFields.Raw);

        Assert.Equal(12, cdb.Length);
        Assert.Equal(0xBE, cdb[0]);
        Assert.Equal((byte)MmcCommands.ExpectedSectorType.Cdda, (byte)((cdb[1] >> 2) & 0x07));
        Assert.Equal(0x00ABCDEFu, BinaryPrimitives.ReadUInt32BigEndian(cdb.AsSpan(2, 4)));
        // Transfer length is a 24-bit big-endian field at bytes 6..8.
        Assert.Equal(27u, (uint)((cdb[6] << 16) | (cdb[7] << 8) | cdb[8]));
        Assert.Equal((byte)MmcCommands.SectorFields.Raw, cdb[9]);
    }

    [Fact]
    public void ReadCd_rejects_transfer_lengths_beyond_24_bits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MmcCommands.ReadCd(0, 0x1000000));
    }

    [Fact]
    public void ReadToc_cdb_requests_lba_addressing_and_format_zero()
    {
        var cdb = MmcCommands.ReadToc();

        Assert.Equal(10, cdb.Length);
        Assert.Equal(0x43, cdb[0]);
        Assert.Equal(0x00, cdb[1] & 0x02);   // MSF = 0 -> LBA addresses
        Assert.Equal(0x00, cdb[2] & 0x0F);   // Format 0 -> TOC
    }

    // --- read planning --------------------------------------------------------

    [Fact]
    public void Data_only_disc_is_planned_cooked_2048()
    {
        var toc = TocParser.Parse(BuildToc(1, new[] { (1, (byte)0x04, 0u) }, 100000));
        var plan = ReadPlanner.Plan(toc, Drive());

        Assert.False(plan.RawMode);
        Assert.Single(plan.Tracks);
        Assert.Equal(CdiSectorSize.S2048, plan.Tracks[0].SectorSize);
        Assert.Equal(CdiTrackMode.Mode1, plan.Tracks[0].Mode);
        Assert.Equal(100000L * 2048, plan.TotalBytes);
    }

    [Fact]
    public void Audio_forces_raw_2352_even_when_not_requested()
    {
        // CD-DA has no cooked form; asking for 2048 would be nonsense.
        var toc = TocParser.Parse(BuildToc(1, new[] { (1, (byte)0x00, 0u) }, 50000));
        var plan = ReadPlanner.Plan(toc, Drive(), preferRaw: false);

        Assert.True(plan.RawMode);
        Assert.Equal(CdiSectorSize.S2352, plan.Tracks[0].SectorSize);
        Assert.Equal(CdiTrackMode.Audio, plan.Tracks[0].Mode);
        Assert.Contains(plan.Warnings, w => w.Contains("audio"));
    }

    [Fact]
    public void Prefer_raw_reads_data_tracks_at_2352()
    {
        var toc = TocParser.Parse(BuildToc(1, new[] { (1, (byte)0x04, 0u) }, 1000));
        var plan = ReadPlanner.Plan(toc, Drive(), preferRaw: true);

        Assert.True(plan.RawMode);
        Assert.Equal(CdiSectorSize.S2352, plan.Tracks[0].SectorSize);
    }

    [Fact]
    public void Mixed_mode_disc_warns_about_writing_it_back()
    {
        var toc = TocParser.Parse(BuildToc(1, new[]
        {
            (1, (byte)0x04, 0u), (2, (byte)0x00, 30000u),
        }, 50000));

        var plan = ReadPlanner.Plan(toc, Drive());

        Assert.True(plan.RawMode);                 // audio present
        Assert.Equal(2, plan.Tracks.Count);
        Assert.Contains(plan.Warnings, w => w.Contains("RAW-capable"));
    }

    [Fact]
    public void Dvd_media_is_always_planned_cooked_2048()
    {
        // DVD sectors are always 2048 bytes; there is no raw 2352 form.
        var toc = TocParser.Parse(BuildToc(1, new[] { (1, (byte)0x04, 0u) }, 2295104));
        var dvd = Drive() with { MediaProfile = MmcProfile.DvdRom };

        var plan = ReadPlanner.Plan(toc, dvd);

        Assert.False(plan.RawMode);
        Assert.Equal(CdiSectorSize.S2048, plan.Tracks[0].SectorSize);
    }

    [Fact]
    public void Raw_requested_on_a_dvd_is_refused_with_a_useful_message()
    {
        // Regression: a raw read of a DVD is rejected by the drive with
        // "Illegal request: invalid field in CDB". Refuse it up front instead.
        var toc = TocParser.Parse(BuildToc(1, new[] { (1, (byte)0x04, 0u) }, 2295104));
        var dvd = Drive() with { MediaProfile = MmcProfile.DvdRom };

        var ex = Assert.Throws<ReadNotSupportedException>(
            () => ReadPlanner.Plan(toc, dvd, preferRaw: true));

        Assert.Contains("CD-only", ex.Message);
    }

    [Fact]
    public void Dvd_only_drive_reading_a_dvd_is_not_refused_for_lacking_cd_read()
    {
        var toc = TocParser.Parse(BuildToc(1, new[] { (1, (byte)0x04, 0u) }, 1000));
        var dvdOnly = Drive(cdRead: false) with { MediaProfile = MmcProfile.DvdRom };

        var plan = ReadPlanner.Plan(toc, dvdOnly);   // must not throw

        Assert.Single(plan.Tracks);
    }

    [Fact]
    public void Cd_media_still_allows_raw()
    {
        var toc = TocParser.Parse(BuildToc(1, new[] { (1, (byte)0x04, 0u) }, 1000));
        var cd = Drive() with { MediaProfile = MmcProfile.CdRom };

        var plan = ReadPlanner.Plan(toc, cd, preferRaw: true);

        Assert.True(plan.RawMode);
        Assert.Equal(CdiSectorSize.S2352, plan.Tracks[0].SectorSize);
    }

    [Fact]
    public void Drive_that_cannot_read_the_media_present_is_refused()
    {
        // CD in a drive with no CD read capability.
        var toc = TocParser.Parse(BuildToc(1, new[] { (1, (byte)0x04, 0u) }, 1000));
        var noCd = Drive(cdRead: false) with { MediaProfile = MmcProfile.CdRom };

        var ex = Assert.Throws<ReadNotSupportedException>(() => ReadPlanner.Plan(toc, noCd));
        Assert.Contains("CdRom", ex.Message);
    }

    [Fact]
    public void Drive_with_no_read_capability_at_all_is_refused()
    {
        var toc = TocParser.Parse(BuildToc(1, new[] { (1, (byte)0x04, 0u) }, 1000));
        var deaf = Drive(cdRead: false) with { DvdRead = false, BdRead = false };

        Assert.Throws<ReadNotSupportedException>(() => ReadPlanner.Plan(toc, deaf));
    }

    [Fact]
    public void Zero_length_tracks_are_skipped_with_a_warning()
    {
        // Two tracks starting at the same LBA => the first has zero length.
        var toc = TocParser.Parse(BuildToc(1, new[]
        {
            (1, (byte)0x04, 0u), (2, (byte)0x04, 0u),
        }, 1000));

        var plan = ReadPlanner.Plan(toc, Drive());

        Assert.Single(plan.Tracks);
        Assert.Contains(plan.Warnings, w => w.Contains("zero length"));
    }
}
