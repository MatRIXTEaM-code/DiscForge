using DiscForge.Core.DvdVideo;
using Xunit;

namespace DiscForge.Core.Tests;

public class DvdStreamDescribeTests
{
    [Fact]
    public void Audio_describe_reads_in_plain_language()
    {
        var a = new IfoReader.AudioStream { Index = 0, Codec = "AC3", Language = "en", Channels = 6 };
        Assert.Equal("Stream 0: AC3, English, 5.1", a.Describe());
    }

    [Fact]
    public void Audio_describe_handles_stereo_and_unknown_language()
    {
        var a = new IfoReader.AudioStream { Index = 1, Codec = "DTS", Language = "  ", Channels = 2 };
        Assert.Equal("Stream 1: DTS, undetermined, stereo", a.Describe());
    }

    [Fact]
    public void An_unmapped_language_code_falls_back_to_the_code()
    {
        var a = new IfoReader.AudioStream { Index = 2, Codec = "LPCM", Language = "xx", Channels = 1 };
        Assert.Equal("Stream 2: LPCM, XX, mono", a.Describe());
    }

    [Fact]
    public void Subtitle_describe_names_the_language_or_says_undetermined()
    {
        Assert.Equal("Subtitle 0: German",
            new IfoReader.SubtitleStream { Index = 0, Language = "de" }.Describe());
        Assert.Equal("Subtitle 2: undetermined",
            new IfoReader.SubtitleStream { Index = 2, Language = "" }.Describe());
    }

    [Fact]
    public void Describe_never_emits_the_raw_record_dump()
    {
        var a = new IfoReader.AudioStream { Index = 0, Codec = "AC3", Language = "en", Channels = 6 };
        var t = new IfoReader.SubtitleStream { Index = 0, Language = "en" };
        Assert.DoesNotContain("{", a.Describe());
        Assert.DoesNotContain("{", t.Describe());
    }
}
