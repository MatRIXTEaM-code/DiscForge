using System;
using System.Collections.Generic;
using DiscForge.Core.DvdVideo;
using Xunit;

namespace DiscForge.Core.Tests;

public class IfoPlanJsonEdgeTests
{
    [Fact]
    public void Empty_plan_is_rejected_by_the_writer()
    {
        var plan = IfoPlanJson.ToPlan(new IfoPlanJson.DvdDto());   // no title sets
        bool threw = false;
        try { IfoWriter.Write(plan); } catch (ArgumentException) { threw = true; }
        Assert.True(threw);
    }

    [Fact]
    public void Invalid_json_throws()
    {
        bool threw = false;
        try { IfoPlanJson.FromJson("{ not valid json"); } catch { threw = true; }
        Assert.True(threw);
    }

    [Fact]
    public void Audio_language_survives_the_round_trip()
    {
        var plan = new IfoWriter.DvdPlan
        {
            TitleSets = new List<IfoWriter.TitleSetPlan>
            {
                new()
                {
                    Number = 1,
                    Titles = new List<IfoWriter.TitlePlan> { new() { Chapters = 1, Angles = 1 } },
                    Audio = new List<IfoWriter.AudioPlan> { new() { Codec = "AC3", Channels = 2, Language = "de" } },
                },
            },
        };
        var rebuilt = IfoPlanJson.ToPlan(IfoPlanJson.FromJson(IfoPlanJson.ToJson(IfoPlanJson.FromPlan(plan))));
        var vts = IfoWriter.Write(rebuilt)["VTS_01_0.IFO"];
        // Audio attribute at 0x204: bytes 2,3 hold the ISO-639 language when present.
        Assert.Equal((byte)'d', vts[0x206]);
        Assert.Equal((byte)'e', vts[0x207]);
    }

    [Fact]
    public void Unknown_audio_codec_is_rejected()
    {
        var dto = new IfoPlanJson.DvdDto();
        dto.TitleSets.Add(new IfoPlanJson.TitleSetDto
        {
            Number = 1,
            Titles = { new IfoPlanJson.TitleDto() },
            Audio = { new IfoPlanJson.AudioDto { Codec = "FLAC" } },   // not a DVD audio codec
        });
        var plan = IfoPlanJson.ToPlan(dto);
        bool threw = false;
        try { IfoWriter.Write(plan); } catch (ArgumentException) { threw = true; }
        Assert.True(threw);
    }

    [Fact]
    public void Duplicate_title_set_numbers_are_rejected()
    {
        var dto = new IfoPlanJson.DvdDto();
        dto.TitleSets.Add(new IfoPlanJson.TitleSetDto { Number = 1, Titles = { new IfoPlanJson.TitleDto() } });
        dto.TitleSets.Add(new IfoPlanJson.TitleSetDto { Number = 1, Titles = { new IfoPlanJson.TitleDto() } });
        var plan = IfoPlanJson.ToPlan(dto);
        bool threw = false;
        try { IfoWriter.Write(plan); } catch (ArgumentException) { threw = true; }
        Assert.True(threw);
    }
}
