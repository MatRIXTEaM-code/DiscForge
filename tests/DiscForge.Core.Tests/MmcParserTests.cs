using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Devices;
using DiscForge.Core.Mmc;
using Xunit;

namespace DiscForge.Core.Tests;

public class MmcParserTests
{
    // ---- INQUIRY ----

    [Fact]
    public void Inquiry_parses_vendor_model_firmware()
    {
        var d = new byte[36];
        d[0] = 0x05; // CD/DVD device type
        Encoding.ASCII.GetBytes("PLEXTOR ").CopyTo(d, 8);   // 8 chars
        Encoding.ASCII.GetBytes("DVDR   PX-716A  ").CopyTo(d, 16); // 16
        Encoding.ASCII.GetBytes("1.09").CopyTo(d, 32);      // 4

        var inq = InquiryData.Parse(d);
        Assert.True(inq.IsOpticalDrive);
        Assert.Equal("PLEXTOR", inq.VendorId);
        Assert.Equal("DVDR   PX-716A", inq.ProductId);
        Assert.Equal("1.09", inq.FirmwareRevision);
    }

    // ---- GET CONFIGURATION ----

    private static byte[] BuildConfig(ushort currentProfile, ushort[] profiles,
                                      bool cdMastering, bool masteringRaw, bool tao)
    {
        var body = new List<byte>();
        void U16(List<byte> l, ushort v) { l.Add((byte)(v >> 8)); l.Add((byte)v); }

        // Header (8 bytes): data length filled later, reserved, current profile.
        var header = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), currentProfile);

        // Profile List feature (0x0000).
        var feat = new List<byte>();
        U16(feat, 0x0000);
        feat.Add(0x03);                       // current+persistent
        feat.Add((byte)(profiles.Length * 4));// additional length
        foreach (var p in profiles) { U16(feat, p); feat.Add(0x00); feat.Add(0x00); }

        if (tao)
        {
            U16(feat, 0x002D); feat.Add(0x03); feat.Add(0x04);
            feat.Add(0); feat.Add(0); feat.Add(0); feat.Add(0);
        }
        if (cdMastering)
        {
            U16(feat, 0x002E); feat.Add(0x03); feat.Add(0x04);
            feat.Add((byte)(masteringRaw ? 0x08 : 0x00)); // bit3 = RAW
            feat.Add(0); feat.Add(0); feat.Add(0);
        }

        body.AddRange(header);
        body.AddRange(feat);
        var arr = body.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(arr.AsSpan(0), (uint)(arr.Length - 4));
        return arr;
    }

    [Fact]
    public void Config_parses_profiles_and_features()
    {
        var bytes = BuildConfig(
            currentProfile: (ushort)MmcProfile.CdR,
            profiles: [(ushort)MmcProfile.CdRom, (ushort)MmcProfile.CdR,
                       (ushort)MmcProfile.DvdRom, (ushort)MmcProfile.DvdMinusRSeq],
            cdMastering: true, masteringRaw: true, tao: true);

        var cfg = ConfigurationInfo.Parse(bytes);
        Assert.Equal(MmcProfile.CdR, cfg.CurrentProfile);
        Assert.True(cfg.HasProfile(MmcProfile.CdRom));
        Assert.True(cfg.HasProfile(MmcProfile.DvdMinusRSeq));
        Assert.True(cfg.CdMastering);
        Assert.True(cfg.CdMasteringRaw);
        Assert.True(cfg.CdTrackAtOnce);
    }

    // ---- MODE PAGE 2A ----

    private static byte[] BuildModeSense2A(byte read, byte write, byte b4, byte b5, byte b6)
    {
        // 8-byte header (no block descriptors), then page 0x2A.
        var page = new byte[2 + 5];
        page[0] = 0x2A; page[1] = 5;
        page[2] = read; page[3] = write; page[4] = b4; page[5] = b5; page[6] = b6;

        var resp = new byte[8 + page.Length];
        // block descriptor length = 0
        page.CopyTo(resp, 8);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(0), (ushort)(resp.Length - 2));
        return resp;
    }

    [Fact]
    public void Page2A_parses_capability_bits()
    {
        // read: CD-R(0x01)+DVD-ROM(0x08); write: CD-R(0x01)+DVD-R(0x10);
        // b4: Mode2Form1(0x10)+MultiSession(0x40); b5: subch(0x04)+C2(0x10);
        // b6: BUF(0x80)
        var resp = BuildModeSense2A(0x09, 0x11, 0x50, 0x14, 0x80);
        var cap = MmCapabilities.ParseFromModeSense10(resp);

        Assert.True(cap.CdRRead);
        Assert.True(cap.DvdRomRead);
        Assert.True(cap.CdRWrite);
        Assert.True(cap.DvdRWrite);
        Assert.True(cap.Mode2Form1);
        Assert.True(cap.MultiSession);
        Assert.True(cap.ReadSubchannel);
        Assert.True(cap.C2Pointers);
        Assert.True(cap.BufferUnderrunProtection);
        Assert.False(cap.CdRwWrite);
    }

    // ---- Capability mapping ----

    [Fact]
    public void Modern_drive_maps_to_no_raw_dao()
    {
        // A 2024-style drive: writes CD/DVD/BD, but no CD Mastering RAW.
        var inq = new InquiryData
        {
            PeripheralDeviceType = 5, VendorId = "HL-DT-ST",
            ProductId = "BD-RE WH16NS60", FirmwareRevision = "1.02",
        };
        var cfg = BuildConfigInfo(MmcProfile.BdRe,
            [MmcProfile.CdRom, MmcProfile.CdR, MmcProfile.DvdRom, MmcProfile.DvdPlusR,
             MmcProfile.BdRom, MmcProfile.BdRe],
            mastering: true, masteringRaw: false, tao: true);

        var caps = DriveCapabilities.Build("\\\\.\\E:", inq, cfg, page2a: null);

        Assert.True(caps.CdWrite);
        Assert.True(caps.DvdWrite);
        Assert.True(caps.BdWrite);
        Assert.False(caps.RawDao96);               // the key modern-drive fact
        Assert.Contains("BD-R", caps.Summary());
    }

    [Fact]
    public void Vintage_plextor_maps_to_raw_dao()
    {
        var inq = new InquiryData
        {
            PeripheralDeviceType = 5, VendorId = "PLEXTOR",
            ProductId = "CD-R PREMIUM", FirmwareRevision = "1.07",
        };
        var cfg = BuildConfigInfo(MmcProfile.CdR,
            [MmcProfile.CdRom, MmcProfile.CdR, MmcProfile.CdRw],
            mastering: true, masteringRaw: true, tao: true);
        var page2a = MmCapabilities.ParseFromModeSense10(
            BuildModeSense2A(0x03, 0x03, 0x50, 0x14, 0x80));

        var caps = DriveCapabilities.Build("\\\\.\\F:", inq, cfg, page2a);

        Assert.True(caps.CdWrite);
        Assert.True(caps.RawDao96);                // unlocks the full toolkit
        Assert.True(caps.RawReadSubchannel);
        Assert.True(caps.C2ErrorReporting);
        Assert.False(caps.BdRead);
        Assert.Contains("RAW DAO", caps.Summary());
    }

    private static ConfigurationInfo BuildConfigInfo(
        MmcProfile current, MmcProfile[] profiles, bool mastering, bool masteringRaw, bool tao)
        => ConfigurationInfo.Parse(BuildConfig(
            (ushort)current, Array.ConvertAll(profiles, p => (ushort)p),
            mastering, masteringRaw, tao));

    // --- READ DISC INFORMATION -----------------------------------------------

    private static byte[] DiscInfoResponse(DiscStatus status, bool erasable,
                                           int sessions = 1, int firstTrack = 1)
    {
        var r = new byte[34];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(r, 32);
        // Byte 2 packs disc status (bits 0-1), last session state (2-3), erasable (4).
        r[2] = (byte)(((int)status & 0x03) | (erasable ? 0x10 : 0x00) | (0x01 << 2));
        r[3] = (byte)firstTrack;
        r[4] = (byte)sessions;
        return r;
    }

    [Fact]
    public void Read_disc_information_cdb_is_well_formed()
    {
        var cdb = MmcCommands.ReadDiscInformation();
        Assert.Equal(10, cdb.Length);
        Assert.Equal(0x51, cdb[0]);
        Assert.Equal(34, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(cdb.AsSpan(7, 2)));
    }

    [Fact]
    public void A_blank_disc_is_reported_as_writable()
    {
        var info = DiscInformation.Parse(DiscInfoResponse(DiscStatus.Empty, erasable: false));

        Assert.True(info.IsBlank);
        Assert.False(info.IsSpent);
        Assert.False(info.NeedsErasing);
        Assert.Equal("blank", info.Describe());
    }

    [Fact]
    public void A_finalised_write_once_disc_is_spent()
    {
        // This is the real case: a written DVD+R DL. The media PROFILE says
        // "DvdPlusRDl" either way — only this says it's unusable.
        var info = DiscInformation.Parse(DiscInfoResponse(DiscStatus.Finalized, erasable: false));

        Assert.False(info.IsBlank);
        Assert.True(info.IsSpent);
        Assert.False(info.NeedsErasing);
        Assert.Contains("write-once", info.Describe());
    }

    [Fact]
    public void A_finalised_rewritable_disc_just_needs_erasing()
    {
        var info = DiscInformation.Parse(DiscInfoResponse(DiscStatus.Finalized, erasable: true));

        Assert.False(info.IsBlank);
        Assert.False(info.IsSpent);
        Assert.True(info.NeedsErasing);
        Assert.Contains("erase", info.Describe());
    }

    [Fact]
    public void An_appendable_disc_is_recognised()
    {
        var info = DiscInformation.Parse(DiscInfoResponse(DiscStatus.Incomplete, erasable: false));

        Assert.Equal(DiscStatus.Incomplete, info.Status);
        Assert.False(info.IsBlank);
        Assert.Contains("appendable", info.Describe());
    }

    [Fact]
    public void The_erasable_bit_is_read_independently_of_status()
    {
        Assert.True(DiscInformation.Parse(DiscInfoResponse(DiscStatus.Empty, true)).Erasable);
        Assert.False(DiscInformation.Parse(DiscInfoResponse(DiscStatus.Empty, false)).Erasable);
    }

    [Fact]
    public void Session_and_track_counts_are_read()
    {
        var info = DiscInformation.Parse(DiscInfoResponse(DiscStatus.Finalized, false, sessions: 3, firstTrack: 1));
        Assert.Equal(3, info.Sessions);
        Assert.Equal(1, info.FirstTrack);
    }

    [Fact]
    public void A_truncated_disc_information_response_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => DiscInformation.Parse(new byte[4]));
    }
}
