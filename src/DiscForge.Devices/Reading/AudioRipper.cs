// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Runtime.Versioning;
using System.Text;
using DiscForge.Core.Audio;
using DiscForge.Core.Mmc;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices.Reading;

/// <summary>Progress through a rip.</summary>
public sealed record AudioRipProgress(
    int TrackNumber, int TrackIndex, int TrackCount,
    uint SectorsDone, uint SectorsTotal, string Detail)
{
    /// <summary>Fraction of the whole rip, not just this track.</summary>
    public double Fraction => TrackCount == 0
        ? 0
        : (TrackIndex + (SectorsTotal == 0 ? 0 : (double)SectorsDone / SectorsTotal)) / TrackCount;
}

/// <summary>What became of one track.</summary>
public sealed record AudioRipTrackResult
{
    public required int Number { get; init; }
    public required string Path { get; init; }
    public required long BytesWritten { get; init; }
    public required int BadSectors { get; init; }
    /// <summary>AccurateRip v1/v2 checksums, for comparison against the database.</summary>
    public required uint AccurateRipV1 { get; init; }
    public required uint AccurateRipV2 { get; init; }
    public bool Clean => BadSectors == 0;
}

public sealed record AudioRipResult
{
    public required IReadOnlyList<AudioRipTrackResult> Tracks { get; init; }
    public required uint DiscId1 { get; init; }
    public required uint DiscId2 { get; init; }
    public required uint CddbId { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required IReadOnlyList<string> Problems { get; init; }

    public bool AllClean => Tracks.All(t => t.Clean);
    public int TotalBadSectors => Tracks.Sum(t => t.BadSectors);

    /// <summary>The URL that fetches this disc's AccurateRip record. Verification
    /// is an online step done separately — DiscForge computes the checksums and
    /// says where to compare them.</summary>
    public string LookupUrl =>
        AccurateRipDatabase.LookupUrl(Tracks.Count, DiscId1, DiscId2, CddbId);
}

public sealed record AudioRipOptions
{
    /// <summary>
    /// Read overlapping chunks and align them by correlation rather than
    /// trusting the drive's positioning.
    ///
    /// CD-DA sectors carry no header, so a drive may return audio a few samples
    /// either side of where it was asked — differently each time. Blind
    /// concatenation then clicks at the joins and drifts across a track. Drives
    /// with "accurate stream" don't jitter and this costs only the overlap
    /// re-read; drives that do jitter need it to rip accurately at all.
    /// </summary>
    public bool CorrectJitter { get; init; } = true;

    /// <summary>Re-reads of a failing sector before accepting silence there.</summary>
    public int RetriesPerSector { get; init; } = 3;

    /// <summary>
    /// Carry on past sectors that never read, filling them with silence and
    /// counting them. Off by default: a rip with silent gaps that isn't
    /// declared as such is worse than one that stops.
    /// </summary>
    public bool ContinueOnError { get; init; }
}

/// <summary>
/// Rips an audio CD to WAV files, one per track, computing AccurateRip
/// checksums as it goes.
///
/// Two things separate a serious ripper from a naive one, and both are here.
///
/// Jitter: CD-DA sectors have no header, so a drive cannot always tell you
/// exactly where it read from. Ask twice and you may get audio offset by a few
/// samples. Concatenating those blindly puts a click at every join. Correlating
/// overlapping reads against what was already written finds the true alignment.
///
/// Verification: a rip that looks fine may not be. AccurateRip's checksums let
/// a rip be compared against everyone else's of the same pressing — if hundreds
/// of people's discs produce the same number as yours, the rip is right. That is
/// a stronger claim than "no errors were reported", because it catches silent
/// mis-reads that report nothing at all.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AudioRipper
{
    /// <summary>Sectors per read. 27 x 2352 = 63,504 bytes, comfortably under
    /// the 64 KB that SPTI handles on every drive.</summary>
    private const uint SectorsPerRead = 27;

    public static AudioRipResult Rip(char driveLetter, AudioRipPlan plan, string outputDirectory,
                                     AudioRipOptions? options = null,
                                     IProgress<AudioRipProgress>? progress = null,
                                     CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(outputDirectory);
        var opts = options ?? new AudioRipOptions();

        Directory.CreateDirectory(outputDirectory);
        var started = DateTime.UtcNow;

        var results = new List<AudioRipTrackResult>();
        var problems = new List<string>();

        using var dev = new SptiDevice(driveLetter);

        for (int i = 0; i < plan.Tracks.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();

            var track = plan.Tracks[i];
            string path = Path.Combine(outputDirectory, track.Filename);

            // Write to a .partial and rename only on success. A WAV header
            // declares its own length, so an interrupted rip leaves a file that
            // looks complete to a player and stops early — worse than one that
            // is obviously unfinished.
            string partial = path + ".partial";

            int bad = 0;
            byte[] pcm;
            try
            {
                pcm = ReadTrackPcm(dev, track, opts, i, plan.Tracks.Count, progress, cancel,
                                   ref bad, problems);
            }
            catch
            {
                TryDelete(partial);
                throw;
            }

            using (var os = File.Create(partial))
            {
                WriteWavHeader(os, pcm.LongLength);
                os.Write(pcm, 0, pcm.Length);
            }

            if (File.Exists(path)) File.Delete(path);
            File.Move(partial, path);

            // AccurateRip weights the first and last tracks differently — their
            // opening and closing samples are excluded, because drives disagree
            // about exactly where a disc begins and ends.
            var checksum = AccurateRip.Compute(
                pcm,
                isFirstTrack: i == 0,
                isLastTrack: i == plan.Tracks.Count - 1);

            results.Add(new AudioRipTrackResult
            {
                Number = track.Number,
                Path = path,
                BytesWritten = pcm.LongLength + 44,
                BadSectors = bad,
                AccurateRipV1 = checksum.V1,
                AccurateRipV2 = checksum.V2,
            });
        }

        var (id1, id2, cddb) = AccurateRip.DiscIds(plan.TocOffsets);

        return new AudioRipResult
        {
            Tracks = results,
            DiscId1 = id1,
            DiscId2 = id2,
            CddbId = cddb,
            Elapsed = DateTime.UtcNow - started,
            Problems = problems,
        };
    }

    /// <summary>
    /// Read the disc's CD-TEXT, if it has any.
    ///
    /// CD-TEXT lives in the lead-in's R-W sub-channel and is entirely optional —
    /// most discs have none, and a drive may refuse to return it even where it
    /// exists. Absence is therefore normal and not worth reporting as a fault.
    ///
    /// Returns album title, album performer, and per-track titles indexed by
    /// track number.
    /// </summary>
    public static CdTextInfo ReadCdText(char driveLetter)
    {
        try
        {
            using var dev = new SptiDevice(driveLetter);

            // READ TOC/PMA/ATIP format 0101b returns the CD-TEXT lead-in data
            // as a series of 18-byte packs.
            var header = new byte[4];
            var r = dev.SendCommand(BuildCdTextCdb(4), header, SptiDataDirection.In,
                                    timeoutSeconds: 15);
            if (!r.Success) return CdTextInfo.None;

            int length = ((header[0] << 8) | header[1]) + 2;
            if (length <= 4 || length > 32768) return CdTextInfo.None;

            var buffer = new byte[length];
            r = dev.SendCommand(BuildCdTextCdb((ushort)length), buffer, SptiDataDirection.In,
                                timeoutSeconds: 15);
            if (!r.Success) return CdTextInfo.None;

            return ParseCdText(buffer.AsSpan(4));
        }
        catch
        {
            // A drive that can't do CD-TEXT is not an error worth surfacing —
            // the rip proceeds with numbered filenames.
            return CdTextInfo.None;
        }
    }

    private static byte[] BuildCdTextCdb(ushort allocationLength)
    {
        var cdb = new byte[10];
        cdb[0] = 0x43;                       // READ TOC/PMA/ATIP
        cdb[2] = 0x05;                       // format 0101b: CD-TEXT
        cdb[7] = (byte)(allocationLength >> 8);
        cdb[8] = (byte)allocationLength;
        return cdb;
    }

    /// <summary>
    /// Parse CD-TEXT packs. Each is 18 bytes: a pack type, the track it applies
    /// to, a sequence number, a block/character indicator, 12 bytes of text, and
    /// a CRC. Text runs across pack boundaries and is null-separated, so a title
    /// is assembled from however many packs it spans.
    /// </summary>
    private static CdTextInfo ParseCdText(ReadOnlySpan<byte> packs)
    {
        // 0x80 = album/track titles, 0x81 = performers.
        var titles = new Dictionary<int, string>();
        var performers = new Dictionary<int, string>();

        CollectPackType(packs, 0x80, titles);
        CollectPackType(packs, 0x81, performers);

        titles.TryGetValue(0, out var albumTitle);
        performers.TryGetValue(0, out var albumPerformer);

        var trackTitles = titles.Where(kv => kv.Key > 0)
                                .ToDictionary(kv => kv.Key, kv => kv.Value);

        return new CdTextInfo
        {
            AlbumTitle = string.IsNullOrWhiteSpace(albumTitle) ? null : albumTitle,
            AlbumPerformer = string.IsNullOrWhiteSpace(albumPerformer) ? null : albumPerformer,
            TrackTitles = trackTitles,
        };
    }

    private static void CollectPackType(ReadOnlySpan<byte> packs, byte type,
                                        Dictionary<int, string> into)
    {
        var text = new StringBuilder();
        int track = -1;

        for (int off = 0; off + 18 <= packs.Length; off += 18)
        {
            var pack = packs.Slice(off, 18);
            if (pack[0] != type) continue;

            int packTrack = pack[1];
            if (track < 0) track = packTrack;

            for (int i = 4; i < 16; i++)
            {
                byte c = pack[i];
                if (c == 0)
                {
                    // A null ends the current string and advances to the next
                    // track — titles are packed end to end across the packs.
                    if (text.Length > 0 && track >= 0 && !into.ContainsKey(track))
                        into[track] = text.ToString().Trim();
                    text.Clear();
                    track++;
                }
                else if (c >= 0x20 && c < 0x7F)
                {
                    text.Append((char)c);
                }
            }
        }

        if (text.Length > 0 && track >= 0 && !into.ContainsKey(track))
            into[track] = text.ToString().Trim();
    }

    /// <summary>
    /// Read one track's PCM, correcting jitter if asked.
    ///
    /// The whole track is held in memory: a CD track is at most about 80 MB and
    /// the checksum needs all of it anyway. Streaming would mean either two
    /// passes or a partial checksum, neither of which is worth the saving.
    /// </summary>
    private static byte[] ReadTrackPcm(SptiDevice dev, AudioRipTrack track, AudioRipOptions opts,
                                       int index, int count,
                                       IProgress<AudioRipProgress>? progress,
                                       CancellationToken cancel,
                                       ref int badSectors, List<string> problems)
    {
        long total = track.PcmBytes;
        var pcm = new byte[total];
        long written = 0;

        // Jitter correction reads a little before where it needs to, and aligns
        // the overlap against what it already has.
        const int OverlapSectors = 2;

        var buffer = new byte[(SectorsPerRead + OverlapSectors) * 2352];
        int corrections = 0, unsure = 0;

        while (written < total)
        {
            cancel.ThrowIfCancellationRequested();

            uint sectorPos = (uint)(written / 2352);
            bool first = written == 0;

            uint back = first || !opts.CorrectJitter
                ? 0
                : Math.Min((uint)OverlapSectors, sectorPos);
            uint readLba = track.StartLba + sectorPos - back;
            uint remaining = track.LengthSectors - (sectorPos - back);
            uint chunk = Math.Min(SectorsPerRead + back, remaining);
            if (chunk == 0) break;

            var span = buffer.AsSpan(0, (int)(chunk * 2352));
            var r = dev.SendCommand(
                MmcCommands.ReadCd(readLba, chunk, MmcCommands.ExpectedSectorType.Cdda,
                                   MmcCommands.SectorFields.UserData),
                span, SptiDataDirection.In, timeoutSeconds: 60);

            if (!r.Success)
            {
                // Narrow it down rather than losing the whole chunk: one bad
                // sector shouldn't cost twenty-seven.
                int recovered = ReadChunkSectorBySector(dev, readLba, chunk, span, opts,
                                                        ref badSectors, problems, cancel);
                if (recovered == 0 && !opts.ContinueOnError)
                    throw new IOException(
                        $"Track {track.Number}: read failed at LBA {readLba:N0} — " +
                        $"{r.Describe()}. Tick \"continue past unreadable sectors\" to fill " +
                        "them with silence instead, and the result will say where.");
            }

            ReadOnlySpan<byte> fresh;
            if (first || !opts.CorrectJitter || back == 0)
            {
                fresh = span;
            }
            else
            {
                // How much of this read we already have.
                int already = (int)(written - (long)(sectorPos - back) * 2352);
                already = Math.Clamp(already, 0, span.Length);

                if (already >= JitterCorrection.MinimumOverlapSamples() * JitterCorrection.BytesPerSample
                    && already <= written)
                {
                    var reference = pcm.AsSpan((int)(written - already), already);
                    fresh = JitterCorrection.NewBytes(reference, span, already, out var alignment);

                    if (!alignment.Confident)
                    {
                        // Silence and pure tones correlate against anything.
                        // Trusting the drive's own positioning beats acting on
                        // a guess.
                        unsure++;
                        fresh = span[already..];
                    }
                    else if (alignment.OffsetSamples != 0)
                    {
                        corrections++;
                    }
                }
                else
                {
                    fresh = span[already..];
                }
            }

            int take = (int)Math.Min(fresh.Length, total - written);
            if (take <= 0) break;
            fresh[..take].CopyTo(pcm.AsSpan((int)written));
            written += take;

            progress?.Report(new AudioRipProgress(
                track.Number, index, count,
                (uint)(written / 2352), track.LengthSectors,
                $"track {track.Number}: {written / 2352:N0}/{track.LengthSectors:N0} sectors"));
        }

        if (corrections > 0 || unsure > 0)
            problems.Add($"Track {track.Number}: {corrections} jitter correction(s), " +
                         $"{unsure} chunk(s) where alignment was not confident " +
                         "(usually silence, which correlates against anything).");

        return pcm;
    }

    /// <summary>
    /// Re-read a failed chunk one sector at a time. Returns how many came back.
    /// Sectors that never read are filled with silence and counted.
    /// </summary>
    private static int ReadChunkSectorBySector(SptiDevice dev, uint startLba, uint count,
                                               Span<byte> into, AudioRipOptions opts,
                                               ref int badSectors, List<string> problems,
                                               CancellationToken cancel)
    {
        int good = 0;
        var one = new byte[2352];

        for (uint i = 0; i < count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            uint lba = startLba + i;

            bool got = false;
            for (int attempt = 0; attempt <= Math.Max(0, opts.RetriesPerSector); attempt++)
            {
                var r = dev.SendCommand(
                    MmcCommands.ReadCd(lba, 1, MmcCommands.ExpectedSectorType.Cdda,
                                       MmcCommands.SectorFields.UserData),
                    one, SptiDataDirection.In, timeoutSeconds: 30);
                if (r.Success) { got = true; break; }
            }

            var slot = into.Slice((int)(i * 2352), 2352);
            if (got)
            {
                one.CopyTo(slot);
                good++;
            }
            else
            {
                // Silence rather than whatever was in the buffer: a burst of
                // noise in a music track is far worse than a gap, and the count
                // says it happened.
                slot.Clear();
                badSectors++;
                if (badSectors <= 20)
                    problems.Add($"LBA {lba:N0}: unreadable, filled with silence.");
            }
        }
        return good;
    }

    private static void WriteWavHeader(Stream s, long pcmBytes)
    {
        const int sampleRate = 44100, channels = 2, bits = 16;
        int byteRate = sampleRate * channels * (bits / 8);
        short blockAlign = channels * (bits / 8);

        using var w = new BinaryWriter(s, Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF"u8.ToArray());
        w.Write((uint)(36 + pcmBytes));
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16u);
        w.Write((short)1);                     // PCM
        w.Write((short)channels);
        w.Write((uint)sampleRate);
        w.Write((uint)byteRate);
        w.Write(blockAlign);
        w.Write((short)bits);
        w.Write("data"u8.ToArray());
        w.Write((uint)pcmBytes);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Album and track names read from a disc's CD-TEXT, where it has any.</summary>
public sealed record CdTextInfo
{
    public string? AlbumTitle { get; init; }
    public string? AlbumPerformer { get; init; }
    public IReadOnlyDictionary<int, string> TrackTitles { get; init; } =
        new Dictionary<int, string>();

    public bool Any => AlbumTitle is not null || TrackTitles.Count > 0;

    public static CdTextInfo None => new();
}