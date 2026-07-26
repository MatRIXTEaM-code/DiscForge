using System.Buffers.Binary;
using DiscForge.Core.Burning;
using DiscForge.Core.Copying;
using DiscForge.Core.Devices;
using DiscForge.Core.Mmc;
using DiscForge.Core.Reading;
using Xunit;

namespace DiscForge.Core.Tests;

public class CopyPlannerTests
{
    // --- helpers -------------------------------------------------------------

    private static byte[] BuildToc((int num, byte control, uint lba)[] tracks, uint leadOut)
    {
        var descs = new List<byte>();
        foreach (var (num, control, lba) in tracks)
        {
            descs.AddRange(new byte[] { 0, (byte)((1 << 4) | (control & 0x0F)), (byte)num, 0 });
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, lba);
            descs.AddRange(b);
        }
        descs.AddRange(new byte[] { 0, (1 << 4) | 0x04, 0xAA, 0 });
        var lo = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lo, leadOut);
        descs.AddRange(lo);

        var body = new List<byte> { 1, (byte)tracks.Max(t => t.num) };
        body.AddRange(descs);
        var resp = new byte[2 + body.Count];
        BinaryPrimitives.WriteUInt16BigEndian(resp, (ushort)body.Count);
        body.CopyTo(resp, 2);
        return resp;
    }

    private static DiscToc DataDisc() =>
        TocParser.Parse(BuildToc(new[] { (1, (byte)0x04, 0u) }, 100000));

    private static DiscToc AudioDisc() =>
        TocParser.Parse(BuildToc(new[]
        {
            (1, (byte)0x00, 0u), (2, (byte)0x00, 20000u),
        }, 50000));

    private static DriveCapabilities Drive(string path, bool raw = false, bool write = true) => new()
    {
        DevicePath = path, Vendor = "TEST", Model = raw ? "PLEXWRITER" : "MODERN-LG",
        FirmwareRevision = "1.0",
        CdRead = true, CdWrite = write, DvdRead = true, DvdWrite = write,
        BdRead = false, BdWrite = false, RawDao96 = raw,
        MediaProfile = MmcProfile.CdRom,
    };

    // --- the whole point: refuse before reading -------------------------------

    [Fact]
    public void Audio_copy_on_a_burner_without_raw_dao_is_refused_before_any_reading()
    {
        // This is the case that matters: reading an audio CD takes minutes.
        // Finding out afterwards that the burner can't write audio back is the
        // worst possible moment. The shape is known from the TOC, so refuse now.
        var job = new CopyJob
        {
            Source = Drive(@"\\.\D:"),
            Destinations = new BurnDestination[] { new BurnDestination.Drive(Drive(@"\\.\E:", raw: false)) },
        };

        var ex = Assert.Throws<BurnNotSupportedException>(() => CopyPlanner.Plan(AudioDisc(), job));
        Assert.Contains("RAW DAO-96", ex.Message);
    }

    [Fact]
    public void Audio_copy_to_a_raw_capable_burner_is_planned()
    {
        var job = new CopyJob
        {
            Source = Drive(@"\\.\D:"),
            Destinations = new BurnDestination[] { new BurnDestination.Drive(Drive(@"\\.\E:", raw: true)) },
        };

        var plan = CopyPlanner.Plan(AudioDisc(), job);

        Assert.True(plan.Shape.HasAudio);
        Assert.Equal(2, plan.Shape.TrackCount);
        Assert.True(plan.Read.RawMode);
        Assert.Single(plan.Burn.Runnable);
        Assert.All(plan.Burn.Runnable, d => Assert.All(d.Steps,
            s => Assert.Equal(BurnMethod.RawDao96, s.Method)));
    }

    [Fact]
    public void Audio_copy_to_an_image_file_works_even_without_a_raw_burner()
    {
        // A file destination has no drive limits — you can always archive it.
        var job = new CopyJob
        {
            Source = Drive(@"\\.\D:"),
            Destinations = new BurnDestination[] { new BurnDestination.ImageFile(@"C:\out\album.cdi") },
        };

        var plan = CopyPlanner.Plan(AudioDisc(), job);
        Assert.Single(plan.Burn.Runnable);
    }

    // --- data discs -----------------------------------------------------------

    [Fact]
    public void Data_disc_copy_uses_the_imapi2_path()
    {
        var job = new CopyJob
        {
            Source = Drive(@"\\.\D:"),
            Destinations = new BurnDestination[] { new BurnDestination.Drive(Drive(@"\\.\E:")) },
        };

        var plan = CopyPlanner.Plan(DataDisc(), job);

        Assert.False(plan.Shape.HasAudio);
        Assert.Equal(1, plan.Shape.TrackCount);
        Assert.False(plan.Read.RawMode);
        Assert.All(plan.Burn.Runnable, d => Assert.All(d.Steps,
            s => Assert.Equal(BurnMethod.Imapi2Data, s.Method)));
    }

    [Fact]
    public void Image_size_is_known_before_reading()
    {
        var job = new CopyJob
        {
            Source = Drive(@"\\.\D:"),
            Destinations = new BurnDestination[] { new BurnDestination.ImageFile(@"C:\out\a.cdi") },
        };

        var plan = CopyPlanner.Plan(DataDisc(), job);

        // 100,000 sectors of cooked data.
        Assert.Equal(100000L * 2048, plan.ImageBytes);
    }

    // --- single-drive copying -------------------------------------------------

    [Fact]
    public void Copying_a_drive_to_itself_is_allowed_and_flagged_for_a_swap()
    {
        // The single-drive case — read to an image, swap the disc, burn. This is
        // exactly why an intermediate image beats copying "on the fly".
        var source = Drive(@"\\.\D:");
        var job = new CopyJob
        {
            Source = source,
            Destinations = new BurnDestination[] { new BurnDestination.Drive(source) },
        };

        var plan = CopyPlanner.Plan(DataDisc(), job);

        Assert.True(plan.RequiresDiscSwap);
        Assert.Contains(plan.Warnings, w => w.Contains("swap"));
        Assert.Single(plan.Burn.Runnable);
    }

    [Fact]
    public void Copying_between_two_drives_needs_no_swap()
    {
        var job = new CopyJob
        {
            Source = Drive(@"\\.\D:"),
            Destinations = new BurnDestination[] { new BurnDestination.Drive(Drive(@"\\.\E:")) },
        };

        var plan = CopyPlanner.Plan(DataDisc(), job);
        Assert.False(plan.RequiresDiscSwap);
    }

    // --- multiple destinations ------------------------------------------------

    [Fact]
    public void One_source_can_feed_several_burners_and_an_archive_file()
    {
        var job = new CopyJob
        {
            Source = Drive(@"\\.\D:"),
            Destinations = new BurnDestination[]
            {
                new BurnDestination.Drive(Drive(@"\\.\E:")),
                new BurnDestination.Drive(Drive(@"\\.\F:")),
                new BurnDestination.ImageFile(@"C:\archive\disc.cdi"),
            },
            Copies = 2,
        };

        var plan = CopyPlanner.Plan(DataDisc(), job);

        Assert.Equal(3, plan.Burn.Runnable.Count());
        // Copies apply to the discs, not the archive file.
        Assert.All(plan.Burn.Runnable.Where(d => !d.IsImageFile),
            d => Assert.Equal(2, d.TotalCopies));
    }

    [Fact]
    public void A_non_writer_destination_sits_out_while_the_others_copy()
    {
        var job = new CopyJob
        {
            Source = Drive(@"\\.\D:"),
            Destinations = new BurnDestination[]
            {
                new BurnDestination.Drive(Drive(@"\\.\E:", write: true)),
                new BurnDestination.Drive(Drive(@"\\.\F:", write: false)),
            },
        };

        var plan = CopyPlanner.Plan(DataDisc(), job);

        Assert.Single(plan.Burn.Runnable);
        Assert.Single(plan.Burn.Refused);
    }

    [Fact]
    public void No_destination_is_refused()
    {
        var job = new CopyJob { Source = Drive(@"\\.\D:"), Destinations = Array.Empty<BurnDestination>() };
        Assert.Throws<BurnNotSupportedException>(() => CopyPlanner.Plan(DataDisc(), job));
    }

    [Fact]
    public void An_unreadable_source_is_refused()
    {
        var deaf = Drive(@"\\.\D:") with { CdRead = false, DvdRead = false, BdRead = false };
        var job = new CopyJob
        {
            Source = deaf,
            Destinations = new BurnDestination[] { new BurnDestination.Drive(Drive(@"\\.\E:")) },
        };

        Assert.Throws<ReadNotSupportedException>(() => CopyPlanner.Plan(DataDisc(), job));
    }
}
