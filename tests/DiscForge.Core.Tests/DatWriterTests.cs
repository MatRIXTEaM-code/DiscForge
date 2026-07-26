using System.Linq;
using DiscForge.Core.Dat;
using Xunit;

namespace DiscForge.Core.Tests;

public class DatWriterTests
{
    [Fact]
    public void A_written_dat_round_trips_through_the_parser()
    {
        string xml =
            "<datafile><header><name>Src</name></header>" +
            "<game name=\"Sonic (USA)\"><rom name=\"Sonic (USA).bin\" size=\"100\" crc=\"aabbccdd\" sha1=\"deadbeef\"/></game>" +
            "<game name=\"Mario &amp; Luigi (Europe)\"><rom name=\"Mario &amp; Luigi (Europe).bin\" size=\"50\" crc=\"11223344\"/></game>" +
            "</datafile>";
        var src = DatFile.ParseText(xml);
        var report = OneGameOneRom.Build(src);

        string written = DatWriter.WriteLogiqx("Src (1G1R)", report.ChosenGames);
        var reparsed = DatFile.ParseText(written);

        // Same rom count, and each original CRC is still findable — including the escaped name.
        Assert.Equal(src.Count, reparsed.Count);
        Assert.NotEmpty(reparsed.ByCrc("aabbccdd"));
        Assert.NotEmpty(reparsed.ByCrc("11223344"));
        Assert.Contains(reparsed.Roms, r => r.Game == "Mario & Luigi (Europe)");
    }

    [Fact]
    public void Header_name_is_written()
    {
        var dat = DatFile.ParseText("<datafile><header><name>X</name></header>" +
            "<game name=\"A (USA)\"><rom name=\"a.bin\" size=\"1\" crc=\"1\"/></game></datafile>");
        var report = OneGameOneRom.Build(dat);
        var written = DatWriter.WriteLogiqx("My Set", report.ChosenGames);
        Assert.Equal("My Set", DatFile.ParseText(written).Name);
    }
}
