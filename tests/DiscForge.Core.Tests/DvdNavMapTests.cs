using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.DvdVideo;
using Xunit;

namespace DiscForge.Core.Tests;

public class DvdNavMapTests
{
    private static void W16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(o), v);
    private static void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o), v);

    // A 4-sector VTS IFO: sector 0 = VTS_MAT, sector 2 = PTT_SRPT, sector 3 = PGCIT.
    // 4 PGCs; PGC1 & PGC4 are title entry points; the PTT references PGC2; PGC3 is orphaned.
    private static byte[] BuildVts(byte pgc3EntryId = 0x00, ushort pttPgcn = 2)
    {
        var ifo = new byte[4 * 2048];
        Encoding.ASCII.GetBytes("DVDVIDEO-VTS").CopyTo(ifo, 0);
        W32(ifo, 0x80, 2);   // PTT_SRPT at sector 2
        W32(ifo, 0xCC, 3);   // PGCIT at sector 3

        int pg = 3 * 2048;
        W16(ifo, pg, 4);                 // 4 PGCs
        W32(ifo, pg + 4, 8 + 4 * 8);     // end address
        ifo[pg + 8 + 0 * 8] = 0x81;      // PGC1: entry, title 1
        ifo[pg + 8 + 1 * 8] = 0x00;      // PGC2: not an entry
        ifo[pg + 8 + 2 * 8] = pgc3EntryId;
        ifo[pg + 8 + 3 * 8] = 0x82;      // PGC4: entry, title 2

        int pt = 2 * 2048;
        W16(ifo, pt, 1);                 // 1 title
        W32(ifo, pt + 4, 15);            // EA: one 4-byte PTT entry at 12..15
        W32(ifo, pt + 8, 12);            // title 1's PTT info at offset 12
        W16(ifo, pt + 12, pttPgcn);      // PTT entry -> PGCN
        W16(ifo, pt + 14, 1);            // PGN
        return ifo;
    }

    [Fact]
    public void An_unreferenced_pgc_is_flagged_as_hidden()
    {
        var r = DvdNavMap.Analyze(BuildVts());
        Assert.Equal(4, r.PgcCount);
        Assert.True(r.HasHidden);
        Assert.Equal(new[] { 3 }, r.HiddenPgcs);

        Assert.True(r.Pgcs[0].IsEntry);            // PGC1
        Assert.Equal(1, r.Pgcs[0].TitleNumber);
        Assert.True(r.Pgcs[1].Referenced);         // PGC2 via PTT
        Assert.False(r.Pgcs[2].Referenced);        // PGC3 hidden
        Assert.True(r.Pgcs[3].IsEntry);            // PGC4
    }

    [Fact]
    public void When_everything_is_reachable_nothing_is_hidden()
    {
        // Make PGC3 an entry point too.
        var r = DvdNavMap.Analyze(BuildVts(pgc3EntryId: 0x83));
        Assert.False(r.HasHidden);
        Assert.Empty(r.HiddenPgcs);
    }

    [Fact]
    public void A_non_vts_ifo_is_rejected()
    {
        var junk = new byte[4 * 2048];
        bool threw = false;
        try { DvdNavMap.Analyze(junk); }
        catch (IfoFormatException) { threw = true; }
        Assert.True(threw);
    }

    [Fact]
    public void A_corrupt_pgc_count_is_clamped_not_crashed()
    {
        var ifo = BuildVts();
        W16(ifo, 3 * 2048, 60000);   // absurd PGC count
        var r = DvdNavMap.Analyze(ifo);
        Assert.True(r.PgcCount < 60000);   // clamped to what fits
    }

    [Fact]
    public void Render_lists_the_pgcs()
    {
        var text = DvdNavMap.Render(DvdNavMap.Analyze(BuildVts()));
        Assert.Contains("UNREFERENCED", text);
        Assert.Contains("PGC 1", text);
    }
}
