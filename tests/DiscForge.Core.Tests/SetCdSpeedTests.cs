using DiscForge.Core.Mmc;
using Xunit;

namespace DiscForge.Core.Tests;

public class SetCdSpeedTests
{
    [Fact]
    public void Cdb_has_the_set_cd_speed_opcode_and_is_12_bytes()
    {
        var cdb = SetCdSpeed.BuildCdb(SetCdSpeed.Max, SetCdSpeed.Max);
        Assert.Equal(12, cdb.Length);
        Assert.Equal(0xBB, cdb[0]);
    }

    [Fact]
    public void Read_and_write_speeds_are_big_endian()
    {
        var cdb = SetCdSpeed.BuildCdb(0x02C0, 0x0160);   // 704 and 352 KB/s
        Assert.Equal(0x02, cdb[2]);
        Assert.Equal(0xC0, cdb[3]);
        Assert.Equal(0x01, cdb[4]);
        Assert.Equal(0x60, cdb[5]);
    }

    [Fact]
    public void Multiplier_scales_by_176_kbs_per_x()
    {
        Assert.Equal(176, SetCdSpeed.KbsForMultiplier(1));
        Assert.Equal(704, SetCdSpeed.KbsForMultiplier(4));
        Assert.Equal(1408, SetCdSpeed.KbsForMultiplier(8));
    }

    [Fact]
    public void Zero_or_negative_multiplier_means_maximum()
    {
        Assert.Equal(SetCdSpeed.Max, SetCdSpeed.KbsForMultiplier(0));
        Assert.Equal(SetCdSpeed.Max, SetCdSpeed.KbsForMultiplier(-3));
    }

    [Fact]
    public void Read_multiplier_cdb_sets_read_and_leaves_write_at_maximum()
    {
        var cdb = SetCdSpeed.BuildReadMultiplier(4);     // 704 = 0x02C0 read, max write
        Assert.Equal(0x02, cdb[2]);
        Assert.Equal(0xC0, cdb[3]);
        Assert.Equal(0xFF, cdb[4]);
        Assert.Equal(0xFF, cdb[5]);
    }
}
