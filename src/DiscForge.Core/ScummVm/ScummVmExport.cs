// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using DiscForge.Core.Audio;
using DiscForge.Core.Cue;
using DiscForge.Core.Files;
using DiscForge.Core.Transcode;

namespace DiscForge.Core.ScummVm;

/// <summary>
/// Exports a CD image into the folder shape ScummVM runs a game from: the data
/// track's files extracted alongside each CD audio track written as
/// <c>trackNN.wav</c> (NN = the disc track number, so a game whose track 1 is data
/// gets <c>track02.wav</c> onward). ScummVM plays FLAC/OGG/MP3/M4A rather than WAV
/// for CD audio, so the audio is emitted as WAV first and, when asked, re-encoded to
/// FLAC or OGG in-process by DiscForge's own encoders — no external tool required.
///
/// CD audio in a BINARY bin/cue is already 16-bit little-endian stereo PCM, exactly
/// what WAV carries, so a track becomes a WAV by prepending a header to its raw
/// sectors — no resampling or byte-swapping.
///
/// Clean-room: this extracts and repackages the user's own disc. Nothing here is
/// protection-related.
/// </summary>
public static class ScummVmExport
{
    private const int RawSector = 2352;   // BINARY bin/cue sector, data and audio alike
    private const int SampleRate = 44100, Channels = 2, Bits = 16;

    /// <summary>The audio-file format written for CD tracks. All three are written by
    /// DiscForge itself; FLAC and OGG are what ScummVM actually plays.</summary>
    public enum AudioFormat { Wav, Flac, Ogg }

    public sealed record AudioTrack(int Number, string Path, long Sectors);

    public sealed record ExportResult
    {
        public required int DataFilesExtracted { get; init; }
        public required IReadOnlyList<AudioTrack> AudioTracks { get; init; }
        /// <summary>The format the audio tracks were actually written in (WAV when a
        /// requested transcode could not run).</summary>
        public required AudioFormat AudioFormatWritten { get; init; }
        public required IReadOnlyList<string> Warnings { get; init; }
    }

    /// <summary>
    /// Export the image at <paramref name="cuePath"/> (a .cue, or any image
    /// <see cref="ImageBrowser"/> can read for the data half) into
    /// <paramref name="outDir"/>: extract the filesystem, then write each CD audio
    /// track. Audio is always written as WAV first (dependency-free); when
    /// <paramref name="format"/> is FLAC or OGG each WAV is re-encoded in-process by
    /// DiscForge's own encoder (no external tool) and removed. ScummVM plays
    /// FLAC/OGG/MP3/M4A but not WAV.
    /// Data-side failures (e.g. no browsable filesystem) are recorded as warnings
    /// rather than aborting the audio export.
    /// </summary>
    public static ExportResult Export(string cuePath, string outDir, AudioFormat format = AudioFormat.Wav,
                                      VorbisEncoder.Quality oggQuality = VorbisEncoder.Quality.Standard)
    {
        ArgumentNullException.ThrowIfNull(cuePath);
        ArgumentNullException.ThrowIfNull(outDir);
        Directory.CreateDirectory(outDir);

        var warnings = new List<string>();

        int dataFiles = 0;
        try
        {
            var listing = ImageBrowser.List(cuePath);
            if (listing.Files.Count > 0)
            {
                var result = ImageBrowser.Extract(cuePath, listing.Files, outDir);
                dataFiles = result.Extracted;
                if (result.Failed > 0)
                    warnings.Add($"{result.Failed} data file(s) could not be extracted.");
            }
            else
            {
                warnings.Add("No files found in the data track's filesystem.");
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Data-track filesystem not extracted: {ex.Message}");
        }

        var audio = ExtractAudioTracks(cuePath, outDir);
        var writtenFormat = AudioFormat.Wav;

        if (audio.Count > 0 && format != AudioFormat.Wav)
        {
            var (transcoded, resultTracks, note) = TranscodeAll(audio, format, oggQuality);
            audio = resultTracks;
            if (note is not null) warnings.Add(note);
            if (transcoded) writtenFormat = format;
        }

        if (audio.Count > 0 && writtenFormat == AudioFormat.Wav)
            warnings.Add(
                $"{audio.Count} audio track(s) written as WAV. ScummVM reads FLAC/OGG/MP3/M4A for CD " +
                "audio, not WAV — transcode them (FLAC/OGG) before running the game in ScummVM.");

        return new ExportResult
        {
            DataFilesExtracted = dataFiles,
            AudioTracks = audio,
            AudioFormatWritten = writtenFormat,
            Warnings = warnings,
        };
    }

    // Convert every WAV track to the requested format, deleting the source WAV on
    // success. Both FLAC and OGG are written in-process by DiscForge's own encoders,
    // so a ScummVM export needs no external dependency such as ffmpeg. Returns
    // whether ALL succeeded, the resulting track list, and a note when off.
    private static (bool AllOk, IReadOnlyList<AudioTrack> Tracks, string? Note) TranscodeAll(
        IReadOnlyList<AudioTrack> wavs, AudioFormat format, VorbisEncoder.Quality oggQuality)
    {
        string ext = format == AudioFormat.Flac ? ".flac" : ".ogg";
        var outTracks = new List<AudioTrack>(wavs.Count);
        foreach (var t in wavs)
        {
            string outPath = System.IO.Path.ChangeExtension(t.Path, ext);
            short[] pcm = ReadWavPcm(t.Path);
            byte[] data = format == AudioFormat.Flac
                ? FlacEncoder.Encode(pcm, SampleRate, Channels)
                : VorbisEncoder.Encode(pcm, SampleRate, Channels, oggQuality);
            File.WriteAllBytes(outPath, data);
            try { File.Delete(t.Path); } catch { /* keep going */ }
            outTracks.Add(t with { Path = outPath });
        }
        return (true, outTracks, null);
    }

    // Read a WAV written by this class (16-bit little-endian PCM after a 44-byte
    // header) back into interleaved samples for the FLAC encoder.
    private static short[] ReadWavPcm(string path)
    {
        var bytes = File.ReadAllBytes(path);
        const int header = 44;
        int pcmBytes = Math.Max(0, bytes.Length - header);
        var samples = new short[pcmBytes / 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)(bytes[header + i * 2] | (bytes[header + i * 2 + 1] << 8));
        return samples;
    }

    /// <summary>
    /// Write each AUDIO track of a bin/cue as <c>track&lt;NN&gt;.wav</c> in
    /// <paramref name="outDir"/> (NN = the cue track number, zero-padded), returning
    /// what was written. Works for both single-file and one-file-per-track cues.
    /// </summary>
    public static IReadOnlyList<AudioTrack> ExtractAudioTracks(string cuePath, string outDir)
    {
        ArgumentNullException.ThrowIfNull(cuePath);
        ArgumentNullException.ThrowIfNull(outDir);
        Directory.CreateDirectory(outDir);

        var cue = CueSheet.Parse(File.ReadAllText(cuePath));
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? ".";
        var sectorsOf = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        long FileSectors(string file)
        {
            if (sectorsOf.TryGetValue(file, out var s)) return s;
            string full = Path.Combine(baseDir, file);
            if (!File.Exists(full))
                throw new FileNotFoundException($"Track file '{file}' referenced by the cue is missing.", full);
            long bytes = new FileInfo(full).Length;
            if (bytes % RawSector != 0)
                throw new InvalidDataException(
                    $"'{file}' is {bytes:N0} bytes, not a whole number of {RawSector}-byte sectors — " +
                    "audio export needs a raw 2352-byte bin.");
            return sectorsOf[file] = bytes / RawSector;
        }

        var written = new List<AudioTrack>();
        for (int i = 0; i < cue.Tracks.Count; i++)
        {
            var t = cue.Tracks[i];
            if (t.Type != CueTrackType.Audio) continue;

            long fileSectors = FileSectors(t.File);
            long start = BinCueMerge.TrackStartSector(t);
            // End at the next track that shares this file, else the file's end. This
            // handles a single big bin (consecutive absolute indices) and Redump's
            // one-bin-per-track set (each track spans its whole file) alike.
            long end = (i + 1 < cue.Tracks.Count && string.Equals(cue.Tracks[i + 1].File, t.File, StringComparison.OrdinalIgnoreCase))
                ? BinCueMerge.TrackStartSector(cue.Tracks[i + 1])
                : fileSectors;
            if (end < start || end > fileSectors)
                throw new InvalidDataException($"Track {t.Number}: computed audio range [{start},{end}) is invalid.");

            string outPath = Path.Combine(outDir, $"track{t.Number:D2}.wav");
            WriteWav(outPath, Path.Combine(baseDir, t.File), start, end - start);
            written.Add(new AudioTrack(t.Number, outPath, end - start));
        }
        return written;
    }

    // Stream a track's raw PCM sectors into a WAV, header first — memory stays flat
    // regardless of track length.
    private static void WriteWav(string outPath, string binPath, long startSector, long sectors)
    {
        long pcmBytes = sectors * RawSector;
        using var dst = File.Create(outPath);
        WriteWavHeader(dst, pcmBytes);

        using var src = File.OpenRead(binPath);
        src.Seek(startSector * RawSector, SeekOrigin.Begin);
        long remaining = pcmBytes;
        var buffer = new byte[1 << 16];
        while (remaining > 0)
        {
            int n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (n <= 0) throw new EndOfStreamException("The bin file is shorter than the cue describes.");
            dst.Write(buffer, 0, n);
            remaining -= n;
        }
    }

    private static void WriteWavHeader(Stream s, long pcmBytes)
    {
        int byteRate = SampleRate * Channels * Bits / 8;
        short blockAlign = (short)(Channels * Bits / 8);
        Span<byte> h = stackalloc byte[44];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)(36 + pcmBytes));
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);          // fmt chunk size
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 1);           // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], Bits);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], (uint)pcmBytes);
        s.Write(h);
    }
}
