// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Rescue;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The ddrescue-style rescue engine against a simulated damaged disc: permanently bad areas, sectors
/// that only read on a later attempt, a read that fails if any sector in it is bad (as drives do).
/// </summary>
public class RescueTests
{
    private const int SS = 2048;

    private sealed class FakeDisc : IRescueSource
    {
        public readonly HashSet<long> Bad = new();
        public readonly Dictionary<long, int> Flaky = new();   // sector -> failures before it reads
        public int Reads, BadReads;
        public int? CancelAfterReads;
        public CancellationTokenSource? Cts;

        public FakeDisc(long sectors) { SectorCount = sectors; }
        public long SectorCount { get; }
        public int SectorSize => SS;

        public static byte ByteAt(long sector, int i) => (byte)((sector * 31 + i * 7 + 1) & 0xFF);

        public bool TryRead(long lba, int count, Span<byte> buffer)
        {
            Reads++;
            if (CancelAfterReads is int c && Reads >= c) Cts!.Cancel();
            bool ok = true;
            for (long s = lba; s < lba + count; s++)
            {
                if (Bad.Contains(s)) ok = false;
                else if (Flaky.TryGetValue(s, out int left) && left > 0) { Flaky[s] = left - 1; ok = false; }
            }
            if (!ok) { BadReads++; return false; }
            for (int k = 0; k < count; k++)
                for (int i = 0; i < SS; i++) buffer[k * SS + i] = ByteAt(lba + k, i);
            return true;
        }
    }

    private static FakeDisc Damaged()
    {
        var d = new FakeDisc(20_000);
        for (long s = 5000; s < 5040; s++) d.Bad.Add(s);     // a scratch
        for (long s = 12_000; s < 12_600; s++) d.Bad.Add(s); // a big damaged area
        d.Bad.Add(19_999);                                    // the very last sector
        d.Flaky[800] = 1;                                     // reads on the second try
        d.Flaky[801] = 1;
        d.Flaky[15_000] = 3;                                  // needs several tries
        return d;
    }

    private static void CheckImage(FakeDisc d, byte[] img, RescueMap map)
    {
        for (long s = 0; s < d.SectorCount; s++)
        {
            var st = map.StatusAt(s * SS);
            if (st == RescueStatus.Finished)
            {
                Assert.Equal(FakeDisc.ByteAt(s, 0), img[s * SS]);
                Assert.Equal(FakeDisc.ByteAt(s, SS - 1), img[s * SS + SS - 1]);
            }
            else Assert.Equal(0, img[s * SS]);
        }
    }

    [Fact]
    public void Rescues_everything_readable_and_maps_exactly_the_bad_sectors()
    {
        var d = Damaged();
        var map = RescueMap.CreateNew(d.SectorCount * SS);
        var ms = new MemoryStream(new byte[d.SectorCount * SS]);
        var r = new RescueEngine(d, ms, map, new RescueOptions { RetryPasses = 3 }).Run();

        Assert.False(r.Cancelled);
        var missing = map.UnfinishedSectors(SS).ToHashSet();
        Assert.True(missing.SetEquals(d.Bad));   // flaky sectors were recovered, truly bad ones weren't
        Assert.Equal(3, map.BadAreas);
        Assert.Equal((d.SectorCount - d.Bad.Count) * SS, map.Rescued);
        Assert.Equal('+', map.CurrentStatus);
        CheckImage(d, ms.ToArray(), map);
    }

    [Fact]
    public void Copying_jumps_away_from_bad_areas()
    {
        // With skipping, a 600-sector damaged area costs far fewer failed reads than one per sector.
        var d = Damaged();
        var map = RescueMap.CreateNew(d.SectorCount * SS);
        new RescueEngine(d, new MemoryStream(new byte[d.SectorCount * SS]), map,
            new RescueOptions { Trim = false, Scrape = false }).Run();
        Assert.True(d.BadReads < 60, $"{d.BadReads} failed reads");
        Assert.Equal(0, map.Total(RescueStatus.NonTried));
        // Nothing good is left behind: everything unread is inside or next to a real bad area.
        foreach (var s in map.UnfinishedSectors(SS))
            Assert.Contains(d.Bad.Concat(d.Flaky.Keys), b => Math.Abs(b - s) <= 32);
    }

    [Fact]
    public void Trimming_and_scraping_touch_each_bad_sector_about_once()
    {
        var d = Damaged();
        var map = RescueMap.CreateNew(d.SectorCount * SS);
        new RescueEngine(d, new MemoryStream(new byte[d.SectorCount * SS]), map).Run();
        Assert.True(d.BadReads < d.Bad.Count + 100, $"{d.BadReads} failed reads for {d.Bad.Count} bad sectors");
    }

    [Fact]
    public void Interrupted_rescue_resumes_to_the_same_result()
    {
        var reference = Damaged();
        var refMap = RescueMap.CreateNew(reference.SectorCount * SS);
        var refImg = new MemoryStream(new byte[reference.SectorCount * SS]);
        new RescueEngine(reference, refImg, refMap, new RescueOptions { RetryPasses = 3 }).Run();

        var d = Damaged();
        var img = new MemoryStream(new byte[d.SectorCount * SS]);
        var map = RescueMap.CreateNew(d.SectorCount * SS);
        string? saved = null;
        foreach (int stopAt in new[] { 150, 400, 900 })
        {
            using var cts = new CancellationTokenSource();
            d.Reads = 0; d.CancelAfterReads = stopAt; d.Cts = cts;
            var r = new RescueEngine(d, img, map, new RescueOptions { RetryPasses = 3 },
                save: (m, _) => saved = m.Format(), cancel: cts.Token).Run();
            Assert.True(r.Cancelled);
            map = RescueMap.Parse(saved!);   // continue from the saved text, as a real resume would
        }
        d.CancelAfterReads = null;
        new RescueEngine(d, img, map, new RescueOptions { RetryPasses = 3 }).Run();

        Assert.Equal(refMap.Format().Split('\n').SkipWhile(l => l.StartsWith('#') || !l.StartsWith("0x")).Skip(1),
                     map.Format().Split('\n').SkipWhile(l => l.StartsWith('#') || !l.StartsWith("0x")).Skip(1));
        Assert.Equal(refImg.ToArray(), img.ToArray());
    }

    [Fact]
    public void A_second_drive_fills_the_gaps_left_by_the_first()
    {
        var first = Damaged();
        var map = RescueMap.CreateNew(first.SectorCount * SS);
        var img = new MemoryStream(new byte[first.SectorCount * SS]);
        new RescueEngine(first, img, map).Run();
        Assert.False(map.IsComplete);

        // A better drive can read the big area, but not the scratch.
        var second = new FakeDisc(first.SectorCount);
        for (long s = 5000; s < 5040; s++) second.Bad.Add(s);
        new RescueEngine(second, img, map).Run();
        Assert.True(second.Reads < 2000, $"{second.Reads} reads — it should only read what was missing");
        Assert.True(map.UnfinishedSectors(SS).ToHashSet().SetEquals(second.Bad));
        CheckImage(second, img.ToArray(), map);
    }

    [Fact]
    public void Map_format_round_trips_and_reads_ddrescue_mapfiles()
    {
        // The example from the GNU ddrescue manual.
        const string ddrescue = """
            # Mapfile. Created by GNU ddrescue version 1.27
            # Command line: ddrescue -d -c18 /dev/fd0 fdimage mapfile
            # Start time:   2015-07-21 09:37:44
            # Current time: 2015-07-21 09:38:19
            # Copying non-tried blocks... Pass 1 (forwards)
            # current_pos  current_status  current_pass
            0x00120000     ?               1
            #      pos        size  status
            0x00000000  0x00117000  +
            0x00117000  0x00000200  -
            0x00117200  0x00001000  /
            0x00118200  0x00007E00  *
            0x00120000  0x00048000  ?
            """;
        var m = RescueMap.Parse(ddrescue);
        Assert.Equal(0x168000, m.Size);
        Assert.Equal(0x117000, m.Rescued);
        Assert.Equal(0x200, m.Total(RescueStatus.BadSector));
        Assert.Equal(0x7E00, m.Total(RescueStatus.NonTrimmed));
        Assert.Equal('?', m.CurrentStatus);
        Assert.Equal(0x120000, m.CurrentPos);
        var again = RescueMap.Parse(m.Format("x", "y"));
        Assert.Equal(m.Blocks, again.Blocks);

        // Decimal and octal are allowed too.
        var d = RescueMap.Parse("0 + 1\n0 4096 +\n010000 4096 -\n");
        Assert.Equal(8192, d.Size);
        Assert.Throws<FormatException>(() => RescueMap.Parse("0 ? 1\n0 100 +\n200 100 ?\n"));   // gap
    }

    [Fact]
    public void Set_splits_and_merges_blocks()
    {
        var m = RescueMap.CreateNew(100 * SS);
        m.Set(10 * SS, 5 * SS, RescueStatus.BadSector);
        m.Set(15 * SS, 5 * SS, RescueStatus.BadSector);
        Assert.Equal(3, m.Blocks.Count);
        Assert.Equal(1, m.BadAreas);
        m.Set(0, 100 * SS, RescueStatus.Finished);
        Assert.True(m.IsComplete);
    }
}
