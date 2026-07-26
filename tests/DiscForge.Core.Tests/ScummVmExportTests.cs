// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using DiscForge.Core.ScummVm;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// ScummVM export — the audio half, which is the new logic. CD audio in a bin/cue is
/// already 16-bit little-endian stereo PCM, so a track becomes <c>trackNN.wav</c> by
/// prepending a WAV header to its raw sectors. These check the naming (disc track
/// number), that the PCM is copied verbatim, and that both single-file and
/// one-file-per-track cue shapes produce the right per-track ranges.
/// </summary>
public class ScummVmExportTests
{
    private const int Sector = 2352;

    private static byte[] Sectors(int count, byte seed)
    {
        var b = new byte[count * Sector];
        for (int i = 0; i < b.Length; i++) b[i] = (byte)(i * 13 + seed);
        return b;
    }

    // Parse a canonical 44-byte PCM WAV header and return (channels, rate, bits, dataLen).
    private static (int ch, int rate, int bits, long dataLen) ReadWavHeader(byte[] wav)
    {
        Assert.Equal("RIFF"u8.ToArray(), wav[0..4]);
        Assert.Equal("WAVE"u8.ToArray(), wav[8..12]);
        Assert.Equal("data"u8.ToArray(), wav[36..40]);
        int ch = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22));
        int rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24));
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34));
        long dataLen = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40));
        return (ch, rate, bits, dataLen);
    }

    [Fact]
    public void SingleFileCue_ExtractsAudioTrack_VerbatimPcm()
    {
        string dir = Directory.CreateTempSubdirectory("dforge_svmx_").FullName;
        try
        {
            // One bin: track 1 data (5 sectors), track 2 audio (8 sectors).
            var data = Sectors(5, 1);
            var audio = Sectors(8, 200);
            var bin = new byte[data.Length + audio.Length];
            data.CopyTo(bin, 0);
            audio.CopyTo(bin, data.Length);
            File.WriteAllBytes(Path.Combine(dir, "game.bin"), bin);

            var cue =
                "FILE \"game.bin\" BINARY\n" +
                "  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n" +
                "  TRACK 02 AUDIO\n    INDEX 01 00:00:05\n";   // audio starts at sector 5
            File.WriteAllText(Path.Combine(dir, "game.cue"), cue);

            var tracks = ScummVmExport.ExtractAudioTracks(Path.Combine(dir, "game.cue"), dir);
            var track = Assert.Single(tracks);
            Assert.Equal(2, track.Number);
            Assert.Equal(8, track.Sectors);
            Assert.EndsWith("track02.wav", track.Path);

            var wav = File.ReadAllBytes(track.Path);
            var (ch, rate, bits, dataLen) = ReadWavHeader(wav);
            Assert.Equal(2, ch);
            Assert.Equal(44100, rate);
            Assert.Equal(16, bits);
            Assert.Equal(audio.Length, dataLen);
            Assert.Equal(audio, wav[44..]);          // PCM copied byte-for-byte
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void MultiFileCue_ExtractsEachAudioTrackFromItsOwnBin()
    {
        string dir = Directory.CreateTempSubdirectory("dforge_svmx_").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "t1.bin"), Sectors(4, 1));       // data
            var a2 = Sectors(6, 50); File.WriteAllBytes(Path.Combine(dir, "t2.bin"), a2);
            var a3 = Sectors(9, 90); File.WriteAllBytes(Path.Combine(dir, "t3.bin"), a3);

            var cue =
                "FILE \"t1.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n" +
                "FILE \"t2.bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 01 00:00:00\n" +
                "FILE \"t3.bin\" BINARY\n  TRACK 03 AUDIO\n    INDEX 01 00:00:00\n";
            File.WriteAllText(Path.Combine(dir, "game.cue"), cue);

            var tracks = ScummVmExport.ExtractAudioTracks(Path.Combine(dir, "game.cue"), dir);
            Assert.Equal(new[] { 2, 3 }, tracks.Select(t => t.Number).ToArray());

            var wav2 = File.ReadAllBytes(Path.Combine(dir, "track02.wav"));
            var wav3 = File.ReadAllBytes(Path.Combine(dir, "track03.wav"));
            Assert.Equal(a2, wav2[44..]);
            Assert.Equal(a3, wav3[44..]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void NoAudioTracks_WritesNothing()
    {
        string dir = Directory.CreateTempSubdirectory("dforge_svmx_").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "d.bin"), Sectors(3, 1));
            File.WriteAllText(Path.Combine(dir, "d.cue"),
                "FILE \"d.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n");

            var tracks = ScummVmExport.ExtractAudioTracks(Path.Combine(dir, "d.cue"), dir);
            Assert.Empty(tracks);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
