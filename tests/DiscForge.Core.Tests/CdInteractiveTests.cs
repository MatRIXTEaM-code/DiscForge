// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;
using DiscForge.Core.CdInteractive;
using DiscForge.Core.Identify;
using DiscForge.Core.Iso;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for Philips CD-i (Green Book) identification and filesystem reading.
/// Real ISO 9660 images are built with <see cref="IsoBuilder"/> and then doctored
/// at sector 16 to synthesise the two CD-i flavours:
///  - <b>pure CD-i</b>: the standard identifier "CD001" is overwritten with "CD-I ";
///  - <b>CD-i Bridge</b>: the standard identifier is left as "CD001" but the system
///    identifier is set to "CD-RTOS CD-BRIDGE".
/// The suite asserts detection, the correct kind, that the file tree reads back, and
/// that a plain ISO / random bytes are never mistaken for CD-i.
/// </summary>
public class CdInteractiveTests
{
    private const int SectorSize = 2048;
    private const int Sector16 = 16 * SectorSize;   // 0x8000
    private const int StandardIdOffset = Sector16 + 1;
    private const int SystemIdOffset = Sector16 + 8;

    // ---- image builders -----------------------------------------------------

    private static byte[] PlainIso() =>
        IsoBuilder.BuildTree("CDI_TEST", new List<IsoBuilder.Node>
        {
            IsoBuilder.Node.File("HELLO.TXT", Encoding.ASCII.GetBytes("hello cd-i")),
            IsoBuilder.Node.Dir("CDI", new List<IsoBuilder.Node>
            {
                IsoBuilder.Node.File("APP.RTF", Encoding.ASCII.GetBytes("application module")),
            }),
        }, joliet: false).Image;

    private static void Overwrite(byte[] img, int at, string ascii)
    {
        var bytes = Encoding.ASCII.GetBytes(ascii);
        Array.Copy(bytes, 0, img, at, bytes.Length);
    }

    /// <summary>A pure CD-i image: "CD001" → "CD-I " at the standard-id field.</summary>
    private static byte[] PureCdiImage()
    {
        var img = PlainIso();
        Overwrite(img, StandardIdOffset, "CD-I ");
        return img;
    }

    /// <summary>A CD-i Bridge image: standard id stays "CD001", system id becomes
    /// "CD-RTOS CD-BRIDGE".</summary>
    private static byte[] BridgeImage()
    {
        var img = PlainIso();
        // Clear the 32-byte system-id field, then write the CD-RTOS marker.
        for (int i = 0; i < 32; i++) img[SystemIdOffset + i] = (byte)' ';
        Overwrite(img, SystemIdOffset, "CD-RTOS CD-BRIDGE");
        return img;
    }

    /// <summary>Wrap a cooked 2048/sector image as raw 2352-byte Mode 2 sectors,
    /// with the user data at offset 24 of each sector.</summary>
    private static byte[] AsRawMode2(byte[] cooked)
    {
        int sectors = cooked.Length / SectorSize;
        var raw = new byte[sectors * 2352];
        for (int s = 0; s < sectors; s++)
            Array.Copy(cooked, s * SectorSize, raw, s * 2352 + 24, SectorSize);
        return raw;
    }

    // ---- detection ----------------------------------------------------------

    [Fact]
    public void Pure_cdi_is_detected()
    {
        using var ms = new MemoryStream(PureCdiImage());
        Assert.True(CdInteractiveReader.IsCdInteractive(ms));
    }

    [Fact]
    public void Bridge_is_detected()
    {
        using var ms = new MemoryStream(BridgeImage());
        Assert.True(CdInteractiveReader.IsCdInteractive(ms));
    }

    [Fact]
    public void Raw_mode2_pure_cdi_is_detected()
    {
        using var ms = new MemoryStream(AsRawMode2(PureCdiImage()));
        Assert.True(CdInteractiveReader.IsCdInteractive(ms));
    }

    [Fact]
    public void Plain_iso_is_not_cdi()
    {
        using var ms = new MemoryStream(PlainIso());
        Assert.False(CdInteractiveReader.IsCdInteractive(ms));
    }

    [Fact]
    public void Random_bytes_are_not_cdi()
    {
        var rnd = new byte[0x9000];
        new Random(99).NextBytes(rnd);
        using var ms = new MemoryStream(rnd);
        Assert.False(CdInteractiveReader.IsCdInteractive(ms));
    }

    // ---- reading ------------------------------------------------------------

    [Fact]
    public void Read_pure_cdi_reports_kind_and_volume()
    {
        using var ms = new MemoryStream(PureCdiImage());
        var disc = CdInteractiveReader.Read(ms);
        Assert.Equal(CdInteractiveKind.PureCdi, disc.Kind);
        Assert.Equal("CDI_TEST", disc.VolumeId);
    }

    [Fact]
    public void Read_bridge_reports_kind_and_system_id()
    {
        using var ms = new MemoryStream(BridgeImage());
        var disc = CdInteractiveReader.Read(ms);
        Assert.Equal(CdInteractiveKind.Bridge, disc.Kind);
        Assert.Contains("CD-RTOS", disc.SystemId);
    }

    [Fact]
    public void Read_pure_cdi_reads_the_file_tree()
    {
        using var ms = new MemoryStream(PureCdiImage());
        var disc = CdInteractiveReader.Read(ms);

        var files = disc.Filesystem.Files.Select(f => f.Path).ToList();
        Assert.Contains(files, p => p.EndsWith("/HELLO.TXT", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, p => p.EndsWith("/APP.RTF", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(disc.Filesystem.Directories, d => d.Name.Equals("CDI", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Read_pure_cdi_extracts_a_file()
    {
        var img = PureCdiImage();
        using var ms = new MemoryStream(img);
        var disc = CdInteractiveReader.Read(ms);

        var hello = disc.Filesystem.Files.First(
            f => f.Path.EndsWith("/HELLO.TXT", StringComparison.OrdinalIgnoreCase));
        // Overlay the standard id back to CD001 so IsoReader.ExtractFile accepts it,
        // mirroring what the reader does internally — extents are unaffected.
        var copy = (byte[])img.Clone();
        Overwrite(copy, StandardIdOffset, "CD001");
        using var patched = new MemoryStream(copy);
        using var outp = new MemoryStream();
        IsoReader.ExtractFile(patched, hello, outp);
        Assert.Equal("hello cd-i", Encoding.ASCII.GetString(outp.ToArray()));
    }

    [Fact]
    public void Read_raw_mode2_pure_cdi_reads_the_tree()
    {
        using var ms = new MemoryStream(AsRawMode2(PureCdiImage()));
        var disc = CdInteractiveReader.Read(ms);
        Assert.Equal(CdInteractiveKind.PureCdi, disc.Kind);
        Assert.Contains(disc.Filesystem.Files,
            f => f.Path.EndsWith("/HELLO.TXT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Read_throws_on_non_cdi()
    {
        using var ms = new MemoryStream(PlainIso());
        Assert.Throws<CdInteractiveFormatException>(() => CdInteractiveReader.Read(ms));
    }

    [Fact]
    public void Read_throws_on_random_bytes()
    {
        var rnd = new byte[0x9000];
        new Random(7).NextBytes(rnd);
        using var ms = new MemoryStream(rnd);
        Assert.Throws<CdInteractiveFormatException>(() => CdInteractiveReader.Read(ms));
    }

    // ---- identifier hook ----------------------------------------------------

    [Fact]
    public void Identify_names_pure_cdi()
    {
        var id = FormatIdentifier.Identify(PureCdiImage());
        Assert.Equal("CD-i", id.Name);
        Assert.Contains("Green Book", id.Detail);
    }

    [Fact]
    public void Identify_names_bridge()
    {
        var id = FormatIdentifier.Identify(BridgeImage());
        Assert.Equal("CD-i", id.Name);
        Assert.Contains("Bridge", id.Detail);
    }

    [Fact]
    public void Identify_leaves_plain_iso_as_iso9660()
    {
        var id = FormatIdentifier.Identify(PlainIso());
        Assert.Equal("ISO 9660", id.Name);
    }
}
