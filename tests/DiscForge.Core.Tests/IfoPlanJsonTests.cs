using System.Collections.Generic;
using DiscForge.Core.DvdVideo;
using Xunit;

namespace DiscForge.Core.Tests;

public class IfoPlanJsonTests
{
    private static IfoWriter.DvdPlan SamplePlan() => new()
    {
        TitleSets = new List<IfoWriter.TitleSetPlan>
        {
            new()
            {
                Number = 1,
                Titles = new List<IfoWriter.TitlePlan> { new() { Chapters = 5, Angles = 2 } },
                Audio = new List<IfoWriter.AudioPlan> { new() { Codec = "AC3", Channels = 6, Language = "en" } },
                Subtitles = new List<IfoWriter.SubtitlePlan> { new() { Language = "fr" } },
            },
            new()
            {
                Number = 2,
                Titles = new List<IfoWriter.TitlePlan> { new() { Chapters = 1, Angles = 1 } },
            },
        },
    };

    [Fact]
    public void Plan_survives_a_json_round_trip_byte_for_byte()
    {
        var plan = SamplePlan();
        var json = IfoPlanJson.ToJson(IfoPlanJson.FromPlan(plan));
        var rebuilt = IfoPlanJson.ToPlan(IfoPlanJson.FromJson(json));

        var a = IfoWriter.Write(plan);
        var b = IfoWriter.Write(rebuilt);

        Assert.Equal(a.Count, b.Count);
        foreach (var kv in a)
        {
            Assert.True(b.ContainsKey(kv.Key));
            Assert.Equal(kv.Value, b[kv.Key]);
        }
    }

    [Fact]
    public void Editing_chapters_in_json_changes_the_rebuilt_ifo()
    {
        var dto = IfoPlanJson.FromPlan(SamplePlan());
        dto.TitleSets[0].Titles[0].Chapters = 12;
        var plan = IfoPlanJson.ToPlan(dto);

        // TT_SRPT entry: chapters is a 16-bit BE value at table+8+2 (sector 1).
        var vmg = IfoWriter.Write(plan)["VIDEO_TS.IFO"];
        int at = IfoWriter.SectorSize + 8 + 2;
        int chapters = (vmg[at] << 8) | vmg[at + 1];
        Assert.Equal(12, chapters);
    }

    [Fact]
    public void Json_is_human_readable_with_field_names()
    {
        var json = IfoPlanJson.ToJson(IfoPlanJson.FromPlan(SamplePlan()));
        Assert.Contains("Chapters", json);
        Assert.Contains("Language", json);
    }
}
