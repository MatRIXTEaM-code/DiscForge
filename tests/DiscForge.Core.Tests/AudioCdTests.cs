using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Audio;
using DiscForge.Core.Cdi;
using DiscForge.Core.Create;
using Xunit;

namespace DiscForge.Core.Tests;

public class AudioCdTests
{
    // --- helpers: build real WAV files ---------------------------------------

    /// <summary>A RIFF/WAVE file. extraChunk inserts a chunk before 'data' to
    /// prove the reader walks the chunk list rather than assuming offset 44.</summary>
    private static byte[] MakeWav(byte[] pcm, int sampleRate = 44100, int channels = 2,
                                  int bits = 16, bool extraChunk = false)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        int blockAlign = channels * (bits / 8);
        int byteRate = sampleRate * blockAlign;

        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(0);                                   // size patched later
        w.Write(Encoding.ASCII.GetBytes("WAVE"));

        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);                            // PCM
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)blockAlign);
        w.Write((short)bits);

        if (extraChunk)
        {
            w.Write(Encoding.ASCII.GetBytes("LIST"));
            w.Write(10);
            w.Write(Encoding.ASCII.GetBytes("INFOhello"));
            w.Write((byte)0);                         // pad to even
        }

        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(pcm.Length);
        w.Write(pcm);

        w.Flush();
        var bytes = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return bytes;
    }

    private static byte[] Tone(int sectors)
    {
        var pcm = new byte[sectors * 2352];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i & 0xFF);
        return pcm;
    }

    private static string TempWav(byte[] wav)
    {
        var p = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".wav");
        File.WriteAllBytes(p, wav);
        return p;
    }

    // --- WAV reading ----------------------------------------------------------

    [Fact]
    public void Reads_a_red_book_wav()
    {
        using var ms = new MemoryStream(MakeWav(Tone(10)));
        var info = WavReader.Read(ms);

        Assert.Equal(44100, info.SampleRate);
        Assert.Equal(2, info.Channels);
        Assert.Equal(16, info.BitsPerSample);
        Assert.True(info.IsCdAudioFormat);
        Assert.Equal(10u, info.SectorCount);
    }

    [Fact]
    public void Finds_the_data_chunk_even_when_other_chunks_precede_it()
    {
        // RIFF is chunked: 'data' is not at a fixed offset. Assuming byte 44
        // works until a file carries a LIST/INFO chunk — which many do.
        var pcm = Tone(3);
        using var ms = new MemoryStream(MakeWav(pcm, extraChunk: true));
        var info = WavReader.Read(ms);

        Assert.Equal(pcm.Length, info.DataLength);
        Assert.NotEqual(44, info.DataOffset);

        var got = new byte[pcm.Length];
        ms.Seek(info.DataOffset, SeekOrigin.Begin);
        ms.ReadExactly(got);
        Assert.Equal(pcm, got);
    }

    [Theory]
    [InlineData(48000, 2, 16, "48000 Hz")]
    [InlineData(44100, 1, 16, "1 channel")]
    [InlineData(44100, 2, 8, "8-bit")]
    public void Non_red_book_audio_is_refused_saying_what_it_actually_is(
        int rate, int channels, int bits, string expected)
    {
        using var ms = new MemoryStream(MakeWav(new byte[2352], rate, channels, bits));
        var ex = Assert.Throws<WavFormatException>(() => WavReader.ReadCdAudio(ms, "x.wav"));
        Assert.Contains(expected, ex.Message);
        Assert.Contains("44100", ex.Message);   // and what it needs to be
    }

    [Fact]
    public void Non_wav_data_is_refused()
    {
        using var ms = new MemoryStream(Encoding.ASCII.GetBytes("this is not a wav file at all"));
        Assert.Throws<WavFormatException>(() => WavReader.Read(ms));
    }

    [Fact]
    public void Duration_is_computed_from_the_pcm_length()
    {
        // 44100 Hz * 2ch * 2 bytes = 176,400 bytes per second.
        using var ms = new MemoryStream(MakeWav(new byte[176400]));
        Assert.Equal(1.0, WavReader.Read(ms).Duration.TotalSeconds, 3);
    }

    // --- compilation ----------------------------------------------------------

    [Fact]
    public void Builds_an_audio_cdi_with_the_mandatory_lead_in_gap()
    {
        var a = TempWav(MakeWav(Tone(100)));
        var b = TempWav(MakeWav(Tone(200)));
        try
        {
            using var cdi = new MemoryStream();
            var result = AudioCdCreator.Create(new[]
            {
                new AudioTrackSource { Path = a },
                new AudioTrackSource { Path = b },
            }, CdiVersion.V35, cdi);

            Assert.Equal(2, result.TrackCount);
            cdi.Position = 0;

            var image = CdiParser.Parse(cdi);
            var tracks = image.AllTracks.ToList();

            Assert.Equal(2, tracks.Count);
            Assert.All(tracks, t => Assert.Equal(CdiTrackMode.Audio, t.Mode));
            Assert.All(tracks, t => Assert.Equal(CdiSectorSize.S2352, t.SectorSize));

            // Track 1 must carry the 150-sector lead-in gap.
            Assert.Equal(150u, tracks[0].PregapSectors);
            Assert.Equal(100u, tracks[0].LengthSectors);
            Assert.Equal(200u, tracks[1].LengthSectors);

            // Track 2 starts after track 1's gap + audio + its own gap.
            Assert.Equal(150u + 100u + 150u, tracks[1].StartLba);
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void Audio_survives_the_round_trip_back_out_of_the_image()
    {
        // The real proof: WAV in, CDI out, extract, and the samples match.
        var pcm = Tone(50);
        var path = TempWav(MakeWav(pcm));
        try
        {
            using var cdi = new MemoryStream();
            AudioCdCreator.Create(new[] { new AudioTrackSource { Path = path } },
                CdiVersion.V35, cdi);
            cdi.Position = 0;

            var image = CdiParser.Parse(cdi);
            var track = image.AllTracks.Single();

            using var wav = new MemoryStream();
            CdiExtractor.ExtractAudioToWav(cdi, track, wav);
            wav.Position = 0;

            var info = WavReader.Read(wav);
            Assert.True(info.IsCdAudioFormat);

            var got = new byte[pcm.Length];
            wav.Seek(info.DataOffset, SeekOrigin.Begin);
            wav.ReadExactly(got);
            Assert.Equal(pcm, got);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Partial_last_sector_is_padded_with_silence_and_reported()
    {
        // A CD has no partial sectors; the tail must be padded, not truncated.
        var pcm = new byte[2352 * 3 + 100];
        var path = TempWav(MakeWav(pcm));
        try
        {
            using var cdi = new MemoryStream();
            var result = AudioCdCreator.Create(new[] { new AudioTrackSource { Path = path } },
                CdiVersion.V35, cdi);

            Assert.Contains(result.Warnings, w => w.Contains("silence"));
            cdi.Position = 0;

            var track = CdiParser.Parse(cdi).AllTracks.Single();
            Assert.Equal(4u, track.LengthSectors);      // rounded up, not down
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Gapless_compilation_is_possible()
    {
        var a = TempWav(MakeWav(Tone(10)));
        var b = TempWav(MakeWav(Tone(10)));
        try
        {
            using var cdi = new MemoryStream();
            AudioCdCreator.Create(new[]
            {
                new AudioTrackSource { Path = a },
                new AudioTrackSource { Path = b, PregapSectors = 0 },
            }, CdiVersion.V35, cdi);
            cdi.Position = 0;

            var tracks = CdiParser.Parse(cdi).AllTracks.ToList();
            Assert.Equal(0u, tracks[1].PregapSectors);
            Assert.Equal(150u + 10u, tracks[1].StartLba);   // straight after track 1
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void Over_length_compilation_is_refused_with_the_running_time()
    {
        // ~81 minutes of audio won't fit any CD.
        var pcm = new byte[81L * 60 * 75 * 2352 / 1];
        var path = TempWav(MakeWav(pcm));
        try
        {
            using var cdi = new MemoryStream();
            var ex = Assert.Throws<AudioCdException>(() =>
                AudioCdCreator.Create(new[] { new AudioTrackSource { Path = path } },
                    CdiVersion.V35, cdi));
            Assert.Contains("won't fit", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Over_74_minutes_warns_that_80_minute_media_is_needed()
    {
        var pcm = new byte[76L * 60 * 75 * 2352];
        var path = TempWav(MakeWav(pcm));
        try
        {
            using var cdi = new MemoryStream();
            var result = AudioCdCreator.Create(new[] { new AudioTrackSource { Path = path } },
                CdiVersion.V35, cdi, allow80Minute: true);
            Assert.Contains(result.Warnings, w => w.Contains("80-minute"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Very_short_track_warns_about_the_red_book_minimum()
    {
        var path = TempWav(MakeWav(Tone(75)));   // 1 second
        try
        {
            using var cdi = new MemoryStream();
            var result = AudioCdCreator.Create(new[] { new AudioTrackSource { Path = path } },
                CdiVersion.V35, cdi);
            Assert.Contains(result.Warnings, w => w.Contains("4 seconds"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Post_gap_appends_silence_and_extends_the_track()
    {
        // A post-gap is silence after the audio, before the lead-out. Some
        // third-party images omit it and a few players clip the final moment.
        var path = TempWav(MakeWav(Tone(100)));
        try
        {
            using var cdi = new MemoryStream();
            var result = AudioCdCreator.Create(new[]
            {
                new AudioTrackSource { Path = path, PostgapSectors = 150 },
            }, CdiVersion.V35, cdi);

            Assert.Contains(result.Warnings, w => w.Contains("post-gap"));
            cdi.Position = 0;

            var track = CdiParser.Parse(cdi).AllTracks.Single();
            // 100 sectors of audio + 150 of post-gap.
            Assert.Equal(250u, track.LengthSectors);
            Assert.Equal(150u, track.PregapSectors);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Post_gap_pushes_the_next_track_later()
    {
        var a = TempWav(MakeWav(Tone(100)));
        var b = TempWav(MakeWav(Tone(100)));
        try
        {
            using var cdi = new MemoryStream();
            AudioCdCreator.Create(new[]
            {
                new AudioTrackSource { Path = a, PostgapSectors = 75 },
                new AudioTrackSource { Path = b },
            }, CdiVersion.V35, cdi);
            cdi.Position = 0;

            var tracks = CdiParser.Parse(cdi).AllTracks.ToList();
            // 150 lead-in + (100 audio + 75 post-gap) + 150 pregap = 475.
            Assert.Equal(175u, tracks[0].LengthSectors);
            Assert.Equal(150u + 175u + 150u, tracks[1].StartLba);
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void No_post_gap_by_default()
    {
        var path = TempWav(MakeWav(Tone(100)));
        try
        {
            using var cdi = new MemoryStream();
            var result = AudioCdCreator.Create(new[] { new AudioTrackSource { Path = path } },
                CdiVersion.V35, cdi);
            cdi.Position = 0;

            Assert.Equal(100u, CdiParser.Parse(cdi).AllTracks.Single().LengthSectors);
            Assert.DoesNotContain(result.Warnings, w => w.Contains("post-gap"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Empty_compilation_is_refused()
    {
        using var cdi = new MemoryStream();
        Assert.Throws<AudioCdException>(() =>
            AudioCdCreator.Create(Array.Empty<AudioTrackSource>(), CdiVersion.V35, cdi));
    }

    [Fact]
    public void Missing_file_is_reported_with_its_track_number()
    {
        using var cdi = new MemoryStream();
        var ex = Assert.Throws<FileNotFoundException>(() =>
            AudioCdCreator.Create(new[] { new AudioTrackSource { Path = @"C:\nope\a.wav" } },
                CdiVersion.V35, cdi));
        Assert.Contains("Track 1", ex.Message);
    }
}
