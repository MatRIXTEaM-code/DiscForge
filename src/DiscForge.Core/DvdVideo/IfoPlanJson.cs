// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscForge.Core.DvdVideo;

/// <summary>
/// A human-editable JSON view of a DVD-Video structural plan (<see cref="IfoWriter.DvdPlan"/>).
/// This is what turns the existing IFO reader/writer pair into an actual *editor*:
/// dump a disc's structure to JSON, change chapter/angle counts or audio/subtitle
/// languages by hand, then rebuild the IFOs from the edited JSON. It is a faithful,
/// lossless projection of the plan — <c>ToJson(FromStructure(x))</c> then
/// <c>ToPlan(FromJson(...))</c> reproduces the same emitted IFOs.
///
/// IFO files are unencrypted even on a CSS disc, so editing them stays inside the
/// clean-room boundary; nothing here touches scrambled video.
/// </summary>
public static class IfoPlanJson
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---- editable DTOs -----------------------------------------------------

    public sealed class TitleDto
    {
        public int Chapters { get; set; } = 1;
        public int Angles { get; set; } = 1;
    }

    public sealed class AudioDto
    {
        public string Codec { get; set; } = "AC3";
        public int Channels { get; set; } = 2;
        public string Language { get; set; } = "";
    }

    public sealed class SubtitleDto
    {
        public string Language { get; set; } = "";
    }

    public sealed class TitleSetDto
    {
        public int Number { get; set; }
        public List<TitleDto> Titles { get; set; } = new();
        public List<AudioDto> Audio { get; set; } = new();
        public List<SubtitleDto> Subtitles { get; set; } = new();
    }

    public sealed class DvdDto
    {
        public List<TitleSetDto> TitleSets { get; set; } = new();
    }

    // ---- conversions -------------------------------------------------------

    public static DvdDto FromPlan(IfoWriter.DvdPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var dto = new DvdDto();
        foreach (var s in plan.TitleSets)
        {
            dto.TitleSets.Add(new TitleSetDto
            {
                Number = s.Number,
                Titles = s.Titles.Select(t => new TitleDto { Chapters = t.Chapters, Angles = t.Angles }).ToList(),
                Audio = s.Audio.Select(a => new AudioDto { Codec = a.Codec, Channels = a.Channels, Language = a.Language }).ToList(),
                Subtitles = s.Subtitles.Select(x => new SubtitleDto { Language = x.Language }).ToList(),
            });
        }
        return dto;
    }

    public static IfoWriter.DvdPlan ToPlan(DvdDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var sets = new List<IfoWriter.TitleSetPlan>();
        foreach (var s in dto.TitleSets)
        {
            sets.Add(new IfoWriter.TitleSetPlan
            {
                Number = s.Number,
                Titles = s.Titles.Select(t => new IfoWriter.TitlePlan { Chapters = t.Chapters, Angles = t.Angles }).ToList(),
                Audio = s.Audio.Select(a => new IfoWriter.AudioPlan { Codec = a.Codec, Channels = a.Channels, Language = a.Language }).ToList(),
                Subtitles = s.Subtitles.Select(x => new IfoWriter.SubtitlePlan { Language = x.Language }).ToList(),
            });
        }
        return new IfoWriter.DvdPlan { TitleSets = sets };
    }

    public static DvdDto FromStructure(IfoReader.DvdStructure structure)
        => FromPlan(IfoWriter.PlanFrom(structure));

    public static string ToJson(DvdDto dto) => JsonSerializer.Serialize(dto, Opts);

    public static DvdDto FromJson(string json)
        => JsonSerializer.Deserialize<DvdDto>(json, Opts)
           ?? throw new ArgumentException("Empty or invalid IFO plan JSON.");
}
