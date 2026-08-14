using DiscForge.Core.Mmc;
using Xunit;

namespace DiscForge.Core.Tests;

public class FullTocTests
{
    private static void Msf(int lba, out byte m, out byte s, out byte f)
    {
        int t = lba + 150;
        m = (byte)(t / (75 * 60));
        int r = t % (75 * 60);
        s = (byte)(r / 75);
        f = (byte)(r % 75);
    }

    // One 11-byte Full-TOC descriptor: session, ADR/CONTROL, TNO, POINT, then the
    // address as MSF in bytes 8-10.
    private static byte[] Desc(int session, byte control, int point, int lba)
    {
        Msf(lba, out var m, out var s, out var f);
        return new byte[] { (byte)session, (byte)((1 << 4) | control), 0, (byte)point, 0, 0, 0, 0, m, s, f };
    }

    private static byte[] FullToc(int firstSession, int lastSession, params byte[][] descs)
    {
        int dataLen = 2 + descs.Length * 11;
        var r = new byte[2 + dataLen];
        r[0] = (byte)(dataLen >> 8);
        r[1] = (byte)(dataLen & 0xFF);
        r[2] = (byte)firstSession;
        r[3] = (byte)lastSession;
        int off = 4;
        foreach (var d in descs) { d.CopyTo(r, off); off += 11; }
        return r;
    }

    [Fact]
    public void The_last_track_of_session_one_is_capped_at_its_own_lead_out()
    {
        // CD Extra shape: two audio tracks in session 1, one data track in session 2.
        // Session 1 lead-out at 1500; session 2 (data) starts at 3000 — the gap 1500..3000
        // must NOT be read as track 2.
        var full = FullToc(1, 2,
            Desc(1, 0x00, 1, 0),
            Desc(1, 0x00, 2, 1000),
            Desc(1, 0x00, 0xA2, 1500),          // session 1 lead-out
            Desc(2, 0x04, 3, 3000),             // data track, session 2
            Desc(2, 0x04, 0xA2, 5000));         // session 2 lead-out

        var toc = TocParser.ParseFullToc(full);

        Assert.Equal(3, toc.Tracks.Count);
        var t2 = toc.Tracks[1];
        Assert.Equal(1000u, t2.StartLba);
        Assert.Equal(500u, t2.LengthSectors);    // 1500 - 1000, NOT 3000 - 1000
        Assert.Equal(1, t2.SessionNumber);

        var t3 = toc.Tracks[2];
        Assert.Equal(3000u, t3.StartLba);
        Assert.Equal(2000u, t3.LengthSectors);   // to session-2 lead-out
        Assert.Equal(2, t3.SessionNumber);
        Assert.True(t3.IsData);
    }

    [Fact]
    public void Multi_session_and_mixed_mode_are_detected()
    {
        var full = FullToc(1, 2,
            Desc(1, 0x00, 1, 0),
            Desc(1, 0x00, 0xA2, 1000),
            Desc(2, 0x04, 2, 3000),
            Desc(2, 0x04, 0xA2, 5000));
        var toc = TocParser.ParseFullToc(full);
        Assert.Equal(2, toc.SessionCount);
        Assert.True(toc.IsMultiSession);
        Assert.True(toc.IsMixedMode);
        Assert.Equal(5000u, toc.LeadOutLba);      // the final lead-out
    }

    // ---- plain (format 0) TOC: an 8-byte descriptor is reserved, ADR/CONTROL, TNO,
    //      reserved, then the LBA big-endian.
    private static byte[] PlainDesc(byte control, int tno, int lba)
        => new byte[] { 0, (byte)((1 << 4) | control), (byte)tno, 0,
                        (byte)(lba >> 24), (byte)(lba >> 16), (byte)(lba >> 8), (byte)lba };

    private static byte[] PlainToc(int first, int last, params byte[][] descs)
    {
        int dataLen = 2 + descs.Length * 8;
        var r = new byte[2 + dataLen];
        r[0] = (byte)(dataLen >> 8);
        r[1] = (byte)(dataLen & 0xFF);
        r[2] = (byte)first;
        r[3] = (byte)last;
        int off = 4;
        foreach (var d in descs) { d.CopyTo(r, off); off += 8; }
        return r;
    }

    [Fact]
    public void Mixed_mode_track_two_starts_at_its_index_one_skipping_the_pregap()
    {
        // Rung 7 shape: a data track 1 at LBA 0, then an audio track 2 whose TOC start address
        // is its INDEX 01 (LBA 450). The 150-sector pregap sits BEFORE that and is not part of
        // the addressable track start — which is why `read-raw --track 2` reads the audio from
        // 450 (skipping the unreadable pregap) in UserData mode.
        var toc = TocParser.Parse(PlainToc(1, 2,
            PlainDesc(0x04, 1, 0),                 // data track
            PlainDesc(0x00, 2, 450),               // audio track, starts at INDEX 01
            PlainDesc(0x00, TocParser.LeadOutTrackNumber, 800)));

        Assert.True(toc.IsMixedMode);
        Assert.Equal(2, toc.Tracks.Count);

        var data = toc.Tracks[0];
        Assert.True(data.IsData);
        Assert.Equal(0u, data.StartLba);
        Assert.Equal(450u, data.LengthSectors);

        var audio = toc.Tracks[1];
        Assert.True(audio.IsAudio);
        Assert.Equal(450u, audio.StartLba);        // INDEX 01 — the pregap is skipped for free
        Assert.Equal(350u, audio.LengthSectors);   // 800 - 450
    }

    [Fact]
    public void A_single_session_full_toc_parses_like_a_normal_disc()
    {
        var full = FullToc(1, 1,
            Desc(1, 0x00, 1, 0),
            Desc(1, 0x00, 2, 2000),
            Desc(1, 0x00, 0xA2, 5000));
        var toc = TocParser.ParseFullToc(full);
        Assert.Equal(1, toc.SessionCount);
        Assert.False(toc.IsMultiSession);
        Assert.Equal(2, toc.Tracks.Count);
        Assert.Equal(2000u, toc.Tracks[0].LengthSectors);   // 2000 - 0
        Assert.Equal(3000u, toc.Tracks[1].LengthSectors);   // 5000 - 2000
    }
}
