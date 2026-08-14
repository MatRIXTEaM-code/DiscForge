using DiscForge.Core.Cue;
using DiscForge.Core.Raw;
using DiscForge.Core.Util;
using Xunit;

namespace DiscForge.Core.Tests;

public class SubchannelTocRecoveryTests
{
    // A synthetic 3-track disc: track 1 data (0..999), track 2 audio (1000..1999),
    // track 3 audio (2000..2499), lead-out at 2500. Q frame per sector.
    private static List<byte[]> BuildDiscQ(bool corruptEvery = false)
    {
        var frames = new List<byte[]>();
        void Emit(int track, int index, QControl ctl, long lba, long trackStart)
        {
            var abs = Msf.FromSectors(lba + 150);
            var rel = Msf.FromSectors(lba - trackStart);          // relative time from index 1 start
            frames.Add(SubQ.Position(ctl, track, index, rel, abs));
        }

        for (long lba = 0; lba < 1000; lba++) Emit(1, 1, QControl.Data, lba, 0);
        for (long lba = 1000; lba < 2000; lba++) Emit(2, 1, QControl.None, lba, 1000);
        for (long lba = 2000; lba < 2500; lba++) Emit(3, 1, QControl.None, lba, 2000);
        // Lead-out frames (TNO 0xAA): build manually since it isn't a BCD track number.
        for (long lba = 2500; lba < 2510; lba++)
        {
            var abs = Msf.FromSectors(lba + 150);
            var q = new byte[12];
            q[0] = 0x01;                          // adr 1, control 0
            q[1] = 0xAA;                          // lead-out
            q[7] = Bcd.From(abs.Minutes); q[8] = Bcd.From(abs.Seconds); q[9] = Bcd.From(abs.Frames);
            ushort crc = Crc16.ComputeInverted(q.AsSpan(0, 10));
            q[10] = (byte)(crc >> 8); q[11] = (byte)crc;
            frames.Add(q);
        }

        if (corruptEvery)
            for (int i = 0; i < frames.Count; i += 7) frames[i][3] ^= 0xFF;   // break CRC on ~1/7 of frames
        return frames;
    }

    [Fact]
    public void Rebuilds_a_clean_toc_from_the_subchannel()
    {
        var toc = SubchannelTocRecovery.Recover(BuildDiscQ());
        Assert.True(toc.Recovered);
        Assert.Equal(3, toc.Tracks.Count);
        Assert.Equal(1, toc.FirstTrack);
        Assert.Equal(3, toc.LastTrack);

        Assert.Equal(0, toc.Tracks[0].StartLba);
        Assert.True(toc.Tracks[0].IsData);
        Assert.Equal(1000, toc.Tracks[1].StartLba);
        Assert.False(toc.Tracks[1].IsData);
        Assert.Equal(2000, toc.Tracks[2].StartLba);
        Assert.Equal(2500, toc.LeadOutLba);
    }

    [Fact]
    public void Recovers_despite_a_damaged_lead_in_and_scattered_bad_frames()
    {
        // The lead-in is gone (we never had it) and ~1 in 7 Q frames is CRC-broken.
        var toc = SubchannelTocRecovery.Recover(BuildDiscQ(corruptEvery: true));
        Assert.Equal(3, toc.Tracks.Count);
        // Using each frame's relative time, the modal vote pins the exact start even with the boundary
        // frame corrupted, as long as any index-1 frame in the track survives.
        Assert.Equal(0, toc.Tracks[0].StartLba);
        Assert.Equal(1000, toc.Tracks[1].StartLba);
        Assert.Equal(2000, toc.Tracks[2].StartLba);
        Assert.True(toc.Tracks[0].IsData);
        Assert.True(toc.FramesRejected > 0);
    }

    [Fact]
    public void Bad_crc_frames_are_rejected_not_trusted()
    {
        var frames = new List<byte[]>();
        // One valid track-5-at-LBA-500 frame, plus a corrupted frame claiming track 9 at LBA 0.
        frames.Add(SubQ.Position(QControl.None, 5, 1, Msf.FromSectors(0), Msf.FromSectors(500 + 150)));
        var bad = SubQ.Position(QControl.None, 9, 1, Msf.FromSectors(0), Msf.FromSectors(150));
        bad[4] ^= 0xFF;                          // corrupt → CRC fails
        frames.Add(bad);

        var toc = SubchannelTocRecovery.Recover(frames);
        Assert.Single(toc.Tracks);
        Assert.Equal(5, toc.Tracks[0].Number);   // the bogus track 9 was rejected
    }

    [Fact]
    public void No_valid_frames_yields_no_toc()
    {
        var toc = SubchannelTocRecovery.Recover(new List<byte[]> { new byte[12] });
        Assert.False(toc.Recovered);
        Assert.Contains("could not rebuild", toc.Summary());
    }
}
