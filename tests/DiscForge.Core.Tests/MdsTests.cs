using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Cdi;
using DiscForge.Core.Convert;
using DiscForge.Core.Mds;
using Xunit;

namespace DiscForge.Core.Tests;

public class MdsTests
{
    // --- a minimal but real MDS builder, mirroring the documented layout -------

    private sealed record T(int Point, MdsTrackMode Mode, int SectorSize,
                            uint Lba, uint Length, uint Pregap, byte Control);

    private static byte[] BuildMds(IEnumerable<T> tracks, MdsMedium medium = MdsMedium.Cd)
    {
        var real = tracks.ToList();
        int firstTrack = real.Min(t => t.Point);
        int lastTrack = real.Max(t => t.Point);
        uint leadOut = real.Max(t => t.Lba + t.Length);

        const int headerSize = 0x58, sessionSize = 0x18, trackSize = 0x50, extraSize = 8, footerSize = 16;
        int nonTrack = 3;                       // A0, A1, A2
        int allBlocks = nonTrack + real.Count;

        int sessionsOff = headerSize;
        int tracksOff = sessionsOff + sessionSize;
        int extrasOff = tracksOff + allBlocks * trackSize;
        int footersOff = extrasOff + real.Count * extraSize;
        int namesOff = footersOff + footerSize;

        var buf = new byte[namesOff + 8];

        // Header
        Encoding.ASCII.GetBytes("MEDIA DESCRIPTOR").CopyTo(buf, 0);
        buf[0x10] = 1; buf[0x11] = 3;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x12), (ushort)medium);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x14), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x50), (uint)sessionsOff);

        // Session
        var s = buf.AsSpan(sessionsOff);
        BinaryPrimitives.WriteInt32LittleEndian(s[0x00..], -150);
        BinaryPrimitives.WriteInt32LittleEndian(s[0x04..], (int)leadOut);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x08..], 1);
        s[0x0A] = (byte)allBlocks;
        s[0x0B] = (byte)nonTrack;
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x0C..], (ushort)firstTrack);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x0E..], (ushort)lastTrack);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x14..], (uint)tracksOff);

        // Lead-in descriptors: A0 = first track, A1 = last track, A2 = lead-out (MSF!)
        void LeadIn(int index, byte point, (int m, int s, int f) pmsf)
        {
            var b = buf.AsSpan(tracksOff + index * trackSize);
            b[0x00] = (byte)MdsTrackMode.None;
            b[0x02] = 0x10;
            b[0x04] = point;
            b[0x09] = (byte)pmsf.m; b[0x0A] = (byte)pmsf.s; b[0x0B] = (byte)pmsf.f;
        }
        LeadIn(0, 0xA0, (firstTrack, 0, 0));
        LeadIn(1, 0xA1, (lastTrack, 0, 0));
        LeadIn(2, 0xA2, MdsParser.LbaToMsf(leadOut));

        // Real tracks
        ulong mdfOffset = 0;
        for (int i = 0; i < real.Count; i++)
        {
            var t = real[i];
            var b = buf.AsSpan(tracksOff + (nonTrack + i) * trackSize);
            b[0x00] = (byte)t.Mode;
            b[0x01] = (byte)MdsSubChannel.None;
            b[0x02] = (byte)((1 << 4) | (t.Control & 0x0F));
            b[0x04] = (byte)t.Point;
            var (m, sec, f) = MdsParser.LbaToMsf(t.Lba);
            b[0x05] = (byte)m; b[0x06] = (byte)sec; b[0x07] = (byte)f;
            BinaryPrimitives.WriteUInt32LittleEndian(b[0x0C..], (uint)(extrasOff + i * extraSize));
            BinaryPrimitives.WriteUInt16LittleEndian(b[0x10..], (ushort)t.SectorSize);
            BinaryPrimitives.WriteUInt32LittleEndian(b[0x24..], t.Lba);
            BinaryPrimitives.WriteUInt64LittleEndian(b[0x28..], mdfOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(b[0x30..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(b[0x34..], (uint)footersOff);

            var e = buf.AsSpan(extrasOff + i * extraSize);
            BinaryPrimitives.WriteUInt32LittleEndian(e[0x00..], t.Pregap);
            BinaryPrimitives.WriteUInt32LittleEndian(e[0x04..], t.Length);

            mdfOffset += (ulong)((t.Pregap + t.Length) * (long)t.SectorSize);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(footersOff), (uint)namesOff);
        return buf;
    }

    // --- MSF ------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(74)]
    [InlineData(75)]
    [InlineData(4499)]
    [InlineData(4500)]
    [InlineData(150000)]
    [InlineData(333000)]
    public void Lba_msf_round_trips(int lba)
    {
        // Regression: the frames field was once dropped when converting back,
        // which silently shifted the lead-out by up to 74 sectors.
        var (m, s, f) = MdsParser.LbaToMsf(lba);
        Assert.Equal(lba, MdsParser.MsfToLba(m, s, f));
    }

    [Fact]
    public void Lba_zero_is_msf_two_seconds()
    {
        Assert.Equal((0, 2, 0), MdsParser.LbaToMsf(0));
    }

    // --- parsing --------------------------------------------------------------

    [Fact]
    public void Parses_single_data_track()
    {
        var mds = BuildMds(new[] { new T(1, MdsTrackMode.Mode1, 2048, 0, 1000, 0, 0x04) });
        var image = MdsParser.Parse(mds);

        Assert.Equal(1, image.VersionMajor);
        Assert.Equal(3, image.VersionMinor);
        Assert.Equal(MdsMedium.Cd, image.Medium);
        var session = Assert.Single(image.Sessions);
        var track = Assert.Single(session.Tracks);

        Assert.Equal(1, track.Point);
        Assert.Equal(2048, track.SectorSize);
        Assert.Equal(1000u, track.LengthSectors);
        Assert.Equal(0ul, track.MdfOffset);
        Assert.False(track.IsAudio);
        Assert.Equal(1000u, session.LeadOutLba);   // decoded from the A2 MSF
    }

    [Fact]
    public void Mdf_offsets_accumulate_across_tracks_including_pregap()
    {
        var mds = BuildMds(new[]
        {
            new T(1, MdsTrackMode.Audio, 2352, 0, 500, 150, 0x00),
            new T(2, MdsTrackMode.Audio, 2352, 500, 700, 0, 0x00),
        });
        var image = MdsParser.Parse(mds);
        var tracks = image.Sessions[0].Tracks;

        Assert.Equal(0ul, tracks[0].MdfOffset);
        // Pregap sectors are stored, so they occupy MDF space.
        Assert.Equal((ulong)((150 + 500) * 2352), tracks[1].MdfOffset);
        Assert.Equal(150u, tracks[0].PregapSectors);
        Assert.True(image.HasAudio);
    }

    [Fact]
    public void Reads_mixed_mode_control_flags()
    {
        var mds = BuildMds(new[]
        {
            new T(1, MdsTrackMode.Mode1, 2048, 0, 30000, 0, 0x04),
            new T(2, MdsTrackMode.Audio, 2352, 30000, 20000, 150, 0x00),
        });
        var image = MdsParser.Parse(mds);
        var tracks = image.Sessions[0].Tracks;

        Assert.Equal(0x04, tracks[0].Control);   // data
        Assert.Equal(0x00, tracks[1].Control);   // audio
        Assert.Equal(50000u, image.Sessions[0].LeadOutLba);
    }

    [Fact]
    public void Lead_in_descriptors_are_not_treated_as_tracks()
    {
        // A0/A1/A2 sit in the same block array as real tracks; only points 1..99
        // carry data.
        var mds = BuildMds(new[] { new T(1, MdsTrackMode.Mode1, 2048, 0, 100, 0, 0x04) });
        var image = MdsParser.Parse(mds);
        Assert.Equal(1, image.TrackCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("MEDIA")]
    public void Rubbish_is_rejected(string content)
    {
        var bytes = Encoding.ASCII.GetBytes(content);
        Assert.Throws<MdsFormatException>(() => MdsParser.Parse(bytes));
    }

    [Fact]
    public void Wrong_signature_is_rejected()
    {
        var bytes = new byte[0x80];
        Encoding.ASCII.GetBytes("NOT A DESCRIPTOR").CopyTo(bytes, 0);
        var ex = Assert.Throws<MdsFormatException>(() => MdsParser.Parse(bytes));
        Assert.Contains("MEDIA DESCRIPTOR", ex.Message);
    }

    [Fact]
    public void Truncated_file_is_rejected_not_misread()
    {
        var mds = BuildMds(new[] { new T(1, MdsTrackMode.Mode1, 2048, 0, 100, 0, 0x04) });
        Assert.Throws<MdsFormatException>(() => MdsParser.Parse(mds.AsSpan(0, 0x60).ToArray()));
    }

    // --- conversion -----------------------------------------------------------

    [Fact]
    public void Converts_mds_mdf_to_cdi_streaming_the_track_data()
    {
        var mds = BuildMds(new[] { new T(1, MdsTrackMode.Mode1, 2048, 0, 10, 0, 0x04) });
        var image = MdsParser.Parse(mds);

        var mdfPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".mdf");
        try
        {
            var payload = new byte[10 * 2048];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);
            File.WriteAllBytes(mdfPath, payload);

            using var cdi = new MemoryStream();
            var result = MdsConverter.MdsToCdi(image, mdfPath, CdiVersion.V35, cdi);

            Assert.Equal(1, result.TrackCount);
            cdi.Position = 0;

            // The result must be a CDI our own parser reads back correctly.
            var parsed = CdiParser.Parse(cdi);
            var t = Assert.Single(parsed.AllTracks);
            Assert.Equal(10u, t.LengthSectors);
            Assert.Equal(CdiSectorSize.S2048, t.SectorSize);

            // And the payload must survive byte-for-byte.
            var extracted = new byte[payload.Length];
            cdi.Position = t.FileOffset;
            cdi.ReadExactly(extracted);
            Assert.Equal(payload, extracted);
        }
        finally { File.Delete(mdfPath); }
    }

    [Fact]
    public void Missing_mdf_is_reported_clearly()
    {
        var mds = BuildMds(new[] { new T(1, MdsTrackMode.Mode1, 2048, 0, 10, 0, 0x04) });
        var image = MdsParser.Parse(mds);

        using var cdi = new MemoryStream();
        var ex = Assert.Throws<FileNotFoundException>(
            () => MdsConverter.MdsToCdi(image, @"C:\nope\missing.mdf", CdiVersion.V35, cdi));
        Assert.Contains(".mds/.mdf pair", ex.Message);
    }

    [Fact]
    public void Mdf_too_short_for_the_descriptor_is_rejected()
    {
        // A mismatched pair must not produce a silently truncated image.
        var mds = BuildMds(new[] { new T(1, MdsTrackMode.Mode1, 2048, 0, 1000, 0, 0x04) });
        var image = MdsParser.Parse(mds);

        var mdfPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".mdf");
        try
        {
            File.WriteAllBytes(mdfPath, new byte[2048]);   // only 1 sector, not 1000
            using var cdi = new MemoryStream();
            var ex = Assert.Throws<InvalidDataException>(
                () => MdsConverter.MdsToCdi(image, mdfPath, CdiVersion.V35, cdi));
            Assert.Contains("matching pair", ex.Message);
        }
        finally { File.Delete(mdfPath); }
    }

    [Fact]
    public void Subchannel_sector_size_is_refused_rather_than_silently_dropped()
    {
        // 2448 = 2352 + 96 bytes of sub-channel. CDI cannot carry it.
        var mds = BuildMds(new[] { new T(1, MdsTrackMode.Audio, 2448, 0, 10, 0, 0x00) });
        var image = MdsParser.Parse(mds);

        var mdfPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".mdf");
        try
        {
            File.WriteAllBytes(mdfPath, new byte[10 * 2448]);
            using var cdi = new MemoryStream();
            var ex = Assert.Throws<InvalidDataException>(
                () => MdsConverter.MdsToCdi(image, mdfPath, CdiVersion.V35, cdi));
            Assert.Contains("sub-channel", ex.Message);
        }
        finally { File.Delete(mdfPath); }
    }

    [Fact]
    public void Default_mdf_path_follows_alcohols_convention()
    {
        Assert.Equal(@"C:\images\disc.mdf", MdsConverter.DefaultMdfPath(@"C:\images\disc.mds"));
    }
}
