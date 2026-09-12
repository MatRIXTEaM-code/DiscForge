// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Runtime.Versioning;
using DiscForge.Core.Audio;
using DiscForge.Core.Cdi;
using DiscForge.Core.Devices;
using DiscForge.Core.Mmc;
using DiscForge.Core.Reading;
using DiscForge.Core.Recovery;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices.Reading;

/// <summary>Progress for a disc read.</summary>
public sealed record ReadProgress(int TrackNumber, uint SectorsDone, uint SectorsTotal, string Detail)
{
    public double Fraction => SectorsTotal == 0 ? 0 : (double)SectorsDone / SectorsTotal;
}

/// <summary>How hard to try, and what to do when a sector simply won't read.</summary>
public sealed record ReadOptions
{
    /// <summary>Re-reads of a single failing sector before giving up on it.
    /// A marginal sector often reads on a later attempt.</summary>
    public int RetriesPerSector { get; init; } = 3;

    /// <summary>
    /// Opt-in: carry on past sectors that never read, filling them with zeros and
    /// recording every one. Off by default — a dump with silent holes is worse
    /// than no dump. Turn it on deliberately to salvage a damaged disc, and treat
    /// the result as partial.
    /// </summary>
    public bool ContinueOnError { get; init; }

    /// <summary>
    /// Opt-in, audio only: read overlapping chunks and align them by correlation
    /// rather than trusting the drive's positioning.
    ///
    /// CD-DA sectors carry no header, so a drive may return audio a few samples
    /// either side of where it was asked — differently each time. Blind
    /// concatenation then clicks at the joins and drifts across a track. Drives
    /// with "accurate stream" don't jitter and this costs only the overlap
    /// re-read (~10% slower); drives that do jitter need it to rip accurately.
    /// </summary>
    public bool CorrectJitter { get; init; }

    /// <summary>
    /// The first and last few sectors of a track sit against a boundary — the
    /// pregap at the head, the lead-out at the tail — where a drive's read-ahead
    /// crosses into territory it cannot type, and it reports "illegal mode for
    /// this track". That is a positioning limit, not disc damage. When set, those
    /// sectors are zero-filled and listed rather than failing the whole read: the
    /// rest of the image is unaffected and the holes are stated plainly.
    /// Independent of ContinueOnError, which governs damage anywhere on the disc.
    /// </summary>
    public bool TolerateBoundarySectors { get; init; } = true;

    /// <summary>
    /// Opt-in, raw sectors only (audio or raw data — READ CD's 2352-byte shape, not a cooked
    /// 2048-byte READ(10)): when a sector's straight retries are exhausted, drive one more
    /// escalation ladder before falling through to the existing boundary/type-rejection handling —
    /// Tier B adaptive re-read (<see cref="DiscForge.Core.Recovery.AdaptiveReread"/> wired to real
    /// hardware via <see cref="DiscForge.Devices.Reading.DriveRereadSource"/>): plain re-reads, then
    /// C2-assisted reads, then a deliberately slow (4x) C2-assisted read — accepting the moment a
    /// data sector's own EDC validates, or, for audio (which has no EDC), the moment every byte has
    /// cross-read consensus. This is the same Tier-B logic already proven against real hardware by
    /// the standalone <c>reread-probe</c> diagnostic; here it runs automatically, in place, for a
    /// sector that would otherwise just be logged as bad. Off by default: it can cost several extra
    /// reads on a genuinely bad sector, so it's reserved for a disc that's already proven marginal
    /// enough to be worth the time.
    /// </summary>
    public bool AdaptiveReread { get; init; }
}

/// <summary>What actually happened during a read.</summary>
public sealed record ReadReport
{
    public required IReadOnlyList<uint> BadSectors { get; init; }
    public required uint SectorsRead { get; init; }

    /// <summary>Sectors zero-filled specifically because they sit against a track
    /// boundary. A subset of BadSectors, split out because the cause is the
    /// drive's geometry rather than the disc's condition.</summary>
    public IReadOnlyList<uint> BoundarySectors { get; init; } = [];

    /// <summary>Anything the read wants to tell the user that isn't a bad sector
    /// — recovered reads, type fallbacks taken, and so on.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>True only if every sector read cleanly — i.e. the image is trustworthy.</summary>
    public bool Complete => BadSectors.Count == 0;

    /// <summary>True if the only holes are at track boundaries. The payload is
    /// intact; those sectors are pregap or run-out padding.</summary>
    public bool CompleteExceptBoundaries =>
        BadSectors.Count > 0 && BadSectors.Count == BoundarySectors.Count;
}

/// <summary>Raised when a sector can't be read and ContinueOnError is off.</summary>
public sealed class DiscReadException(string message) : IOException(message);

/// <summary>
/// Reads a disc to a CDI image. Transport and OS calls live here; the TOC
/// parsing and the decision about what to read at which sector size are done by
/// the pure, tested code in Core (TocParser / ReadPlanner).
///
/// Reads are issued with READ CD (0xBE) rather than READ(10) because only READ CD
/// can return raw 2352-byte sectors and audio.
///
/// Read errors are surfaced, never silently zero-filled: a dump you can't trust
/// is worse than no dump. The one deliberate exception is a track's boundary
/// sectors, which are a drive limitation rather than a disc fault — see ReadOptions.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DiscReader
{
    /// <summary>Sectors per READ CD request. 27 × 2352 = 63,504 bytes — under the
    /// 64 KB that SPTI handles comfortably on every drive.</summary>
    private const uint SectorsPerRead = 27;

    /// <summary>How close to the end of a track counts as "against the lead-out".
    /// Observed failures start at end−2; 4 gives margin without masking real damage.</summary>
    private const uint TailWindowSectors = 4;

    /// <summary>How much of a track's head is pregap. The Red Book pregap is 150
    /// sectors (two seconds); sectors within it belong to the track but often
    /// carry a different mode, or refuse to read at all.</summary>
    private const uint HeadWindowSectors = 150;

    /// <summary>Sense codes worth naming. ASC 0x24 is a malformed CDB field;
    /// 0x64 is the drive refusing the sector type for that track — which happens
    /// on a genuine type mismatch and at both track boundaries.</summary>
    private static class Asc
    {
        public const byte InvalidFieldInCdb = 0x24;
        public const byte IllegalModeForThisTrack = 0x64;
        public const byte CopyProtected = 0x6F;
        public const byte MediumNotPresent = 0x3A;
    }

    private static bool IsTypeRejection(in SptiResult r) =>
        r.SenseKey == 0x05 && (r.Asc == Asc.InvalidFieldInCdb || r.Asc == Asc.IllegalModeForThisTrack);

    /// <summary>Is this sector close enough to either end of its track that a
    /// refusal is more likely geometry than damage?</summary>
    private static bool IsBoundarySector(ReadTrackPlan track, uint lba)
    {
        uint end = track.StartLba + track.LengthSectors;
        bool head = lba < track.StartLba + HeadWindowSectors;
        bool tail = lba + TailWindowSectors >= end;
        return head || tail;
    }

    /// <summary>Fetch and parse the disc's table of contents.</summary>
    public static DiscToc ReadToc(char driveLetter)
    {
        using var dev = new SptiDevice(driveLetter);
        return ReadToc(dev);
    }

    /// <summary>Fetch and parse the disc's table of contents using an already-open device
    /// (so a caller mid-read doesn't have to open a second handle to the same drive).</summary>
    public static DiscToc ReadToc(SptiDevice dev)
    {
        ArgumentNullException.ThrowIfNull(dev);

        // Prefer the Full TOC (format 0010b): it carries a session number on every
        // entry and a lead-out PER session, so the last track of an earlier session
        // is capped at that session's lead-out rather than running into the
        // unreadable inter-session gap. This is what a CD Extra / mixed-mode
        // (audio + data) disc needs — without it, reading the last audio track fails
        // with "illegal mode for this track". It is also correct for single-session
        // discs, so we use it whenever the drive returns a parseable response.
        var full = new byte[4096];
        var fr = dev.SendCommand(MmcCommands.ReadTocFormat(MmcCommands.TocFormat.FullToc, 4096),
                                 full, SptiDataDirection.In);
        if (fr.Success)
        {
            try
            {
                var fullToc = TocParser.ParseFullToc(full);
                if (fullToc.Tracks.Count > 0) return fullToc;
            }
            catch { /* fall back to the plain TOC below */ }
        }

        var buffer = new byte[4096];
        var result = dev.SendCommand(MmcCommands.ReadToc(), buffer, SptiDataDirection.In);
        if (!result.Success)
            throw new IOException($"READ TOC failed: {result.Describe()}");
        return TocParser.Parse(buffer);
    }

    /// <summary>
    /// One line per planned track, for the session log. Worth emitting before any
    /// read: when a track is rejected, the first question is always whether we
    /// classified it correctly, and this answers it without a debugger.
    /// </summary>
    public static IReadOnlyList<string> DescribePlan(ReadPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var lines = new List<string>(plan.Tracks.Count);
        foreach (var t in plan.Tracks)
        {
            uint endLba = t.StartLba + t.LengthSectors;
            lines.Add($"track {t.Number}: {(t.IsAudio ? "audio" : "data")} mode={t.Mode} " +
                      $"lba={t.StartLba:N0}..{endLba - 1:N0} sectors={t.LengthSectors:N0} " +
                      $"sectorSize={(int)t.SectorSize} " +
                      $"request={(t.IsAudio ? "CD-DA/UserData" : "Any/Raw")}");
        }
        return lines;
    }

    /// <summary>
    /// Try a single sector of every planned track before committing to a rip.
    /// Returns null if the plan works, or a human explanation if it doesn't.
    ///
    /// Worth doing: a drive that can't honour the plan rejects the very first
    /// read, and finding that out instantly beats discovering it after writing a
    /// part-file. Not every drive can read raw 2352-byte sectors — plenty of
    /// modern ones can't — and there's no reliable capability bit to ask.
    /// </summary>
    public static string? Probe(char driveLetter, ReadPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using var dev = new SptiDevice(driveLetter);

        foreach (var t in plan.Tracks)
        {
            int sectorBytes = (int)t.SectorSize;
            bool cooked = t.SectorSize == CdiSectorSize.S2048;
            var expected = t.IsAudio
                ? MmcCommands.ExpectedSectorType.Cdda
                : MmcCommands.ExpectedSectorType.Any;

            var one = new byte[sectorBytes];

            // The first sectors of a track are pregap: they belong to the track
            // but often carry a different mode, or refuse to read at all. Probing
            // only sector zero condemns a track for its least representative part,
            // so try a little way in before concluding anything.
            SptiResult r = default;
            uint[] probeOffsets = [0, 16, 150, 300];
            uint probedAt = t.StartLba;
            foreach (uint off in probeOffsets)
            {
                if (off >= t.LengthSectors) break;
                probedAt = t.StartLba + off;
                r = Issue(dev, probedAt, 1, one, cooked, expected);
                if (r.Success) break;
            }

            if (r.Success) continue;

            if (r.Asc == Asc.CopyProtected)
                return $"Track {t.Number}: {r.Describe()}. DiscForge does not read " +
                       "encrypted discs.";

            // A raw read may still work with a different sector type. Two things
            // provoke a rejection: an explicit type the drive dislikes (try Mode 1
            // or Mode 2), and a track we've classified the wrong way round.
            if (!cooked && IsTypeRejection(r))
            {
                foreach (var alt in AlternativeTypes(t.IsAudio, expected))
                {
                    r = Issue(dev, probedAt, 1, one, cooked, alt);
                    if (r.Success) break;
                }
                if (r.Success) continue;
            }

            if (r.SenseKey == 0x05 && r.Asc == Asc.InvalidFieldInCdb && !cooked)
                return $"Track {t.Number}: the drive rejected a raw {sectorBytes}-byte read " +
                       $"({r.Describe()}). Many drives cannot read raw sectors at all. " +
                       (t.IsAudio
                           ? "Audio requires raw reads, so this drive cannot rip this disc."
                           : "Untick \"read data tracks raw\" to read this disc normally.");

            if (r.Asc == Asc.IllegalModeForThisTrack)
                return $"Track {t.Number}: the drive will not serve this track as " +
                       $"{(t.IsAudio ? "audio" : "data")} at LBA {probedAt:N0} ({r.Describe()}). " +
                       "Every sector type was tried. The track may be a different type than " +
                       "its TOC flags claim, or the drive may not support this sector type. " +
                       "Try the other raw/cooked setting for this disc.";

            // A cooked (2048-byte, DVD-mode) track that READ(10) flatly refuses is worth one
            // more try before giving up: READ CD asking for Mode 1 user data goes through a
            // different part of the drive's firmware than READ(10) does, and on some drives it
            // succeeds where READ(10) doesn't — notably on discs that report a normal-looking
            // DVD-ROM TOC (so detection and this probe get this far) but use a non-standard
            // sector encoding underneath, such as a GameCube disc read on an unmodified PC DVD
            // drive. This mirrors the existing "Rung 3" fallback used later during a full read
            // (see TryHarder) — reusing an already-proven command rather than adding a new one.
            // Doing it here too means a disc this drive can actually serve via READ CD isn't
            // rejected before the rip even starts.
            if (cooked)
            {
                var altResult = dev.SendCommand(
                    MmcCommands.ReadCd(probedAt, 1, MmcCommands.ExpectedSectorType.Mode1,
                                       MmcCommands.SectorFields.UserData),
                    one, SptiDataDirection.In, timeoutSeconds: 60);
                if (altResult.Success) continue;

                // Last resort: some discs (a GameCube disc read on an unmodified PC DVD drive is
                // the known real-world case) use sector data that fails a drive's normal EDC/ECC
                // check outright, no matter which read command asks for it — the data itself
                // looks "wrong" to the drive's error correction, even though it's exactly what
                // the disc's spiral carries. Community GameCube-dumping tools get past this with
                // a "streaming" read that tells the drive to hand back the bytes without
                // insisting they check out. DiscForge has no vendor-specific equivalent of that,
                // but the same effect is available through a standard SCSI/MMC mode page (0x01,
                // Read-Write Error Recovery): RC (Read Continuous) asks the drive to prioritise
                // handing back a continuous stream of data over fully recovering it, and DCR
                // (Disable Correction) turns off its ECC correction pass; Read Retry Count = 0
                // stops it re-trying before giving up. Tried only here, after every normal
                // command shape has already failed — and always restored immediately afterwards,
                // success or failure, so this drive's error-recovery behavior for every OTHER
                // read (this track, this disc, or the next one) is left exactly as it was.
                if (TryStreamingRecoveryRead(dev, probedAt, 1, one, cooked, expected))
                    continue;
            }

            return $"Track {t.Number}: test read at LBA {probedAt:N0} failed — {r.Describe()}.";
        }

        return null;
    }

    /// <summary>
    /// Sector types worth trying when the drive rejects our first choice, in order.
    /// Mode 2 matters: on a CD-XA or mixed-mode disc a data track is Form 1 or
    /// Form 2, and a drive that won't infer the type from "Any" rejects everything
    /// until it's named explicitly.
    /// </summary>
    private static IEnumerable<MmcCommands.ExpectedSectorType> AlternativeTypes(
        bool isAudio, MmcCommands.ExpectedSectorType alreadyTried)
    {
        var order = isAudio
            ? new[]
            {
                MmcCommands.ExpectedSectorType.Any,
                MmcCommands.ExpectedSectorType.Mode1,
                MmcCommands.ExpectedSectorType.Mode2Form1,
            }
            : new[]
            {
                MmcCommands.ExpectedSectorType.Mode1,
                MmcCommands.ExpectedSectorType.Mode2Form1,
                MmcCommands.ExpectedSectorType.Mode2,
                MmcCommands.ExpectedSectorType.Mode2Form2,
                MmcCommands.ExpectedSectorType.Any,
                MmcCommands.ExpectedSectorType.Cdda,
            };

        foreach (var t in order)
            if (t != alreadyTried)
                yield return t;
    }

    /// <summary>
    /// Read a whole disc into <paramref name="output"/> as a CDI image. Returns a
    /// report listing any sectors that could not be read — check
    /// <see cref="ReadReport.Complete"/> before trusting the image.
    /// Sessions are not yet distinguished — everything is written as one session,
    /// which is correct for the single-session discs this handles today.
    /// </summary>
    public static ReadReport ReadToCdi(char driveLetter, ReadPlan plan, CdiVersion version,
                                       Stream output, IProgress<ReadProgress>? progress = null,
                                       ReadOptions? options = null,
                                       CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);
        options ??= new ReadOptions();

        using var dev = new SptiDevice(driveLetter);

        var badSectors = new List<uint>();
        var boundarySectors = new List<uint>();
        var notes = new List<string>();

        var inputs = new List<CdiWriter.TrackInput>();
        foreach (var t in plan.Tracks)
        {
            var track = t;
            inputs.Add(new CdiWriter.TrackInput
            {
                Mode = track.Mode,
                SectorSize = track.SectorSize,
                PregapSectors = 0,
                LengthSectors = track.LengthSectors,
                StartLba = track.StartLba,
                Filename = $"TRACK{track.Number:D2}.BIN",
                // Streamed: a full CD is ~700 MB and a DVD far more, so track
                // data goes straight from the drive to the output.
                DataWriter = os => ReadTrack(dev, track, os, progress, cancel, options,
                                             badSectors, boundarySectors, notes),
            });
        }

        CdiWriter.Write(output, version, new[] { (IReadOnlyList<CdiWriter.TrackInput>)inputs });

        return new ReadReport
        {
            BadSectors = badSectors,
            SectorsRead = plan.TotalSectors,
            BoundarySectors = boundarySectors,
            Notes = notes,
        };
    }

    /// <summary>
    /// Read one track's sectors to <paramref name="output"/>, exactly as <see cref="ReadToCdi"/> does
    /// internally for every track in a plan. Exposed (visibility only — the body is unchanged from
    /// what <see cref="ReadToCdi"/> has always called) so a caller can capture one track at a time
    /// into its own file and assemble the final CDI later — the basis for track-granularity resume,
    /// where a track that already read cleanly on a previous attempt is reused instead of re-read.
    /// </summary>
    public static void ReadTrack(SptiDevice dev, ReadTrackPlan track, Stream output,
                                  IProgress<ReadProgress>? progress, CancellationToken cancel,
                                  ReadOptions options, List<uint> badSectors,
                                  List<uint> boundarySectors, List<string> notes)
    {
        // Jitter correction only applies to audio: data sectors carry headers, so
        // the drive can position exactly and there's nothing to correct.
        if (options.CorrectJitter && track.IsAudio)
        {
            ReadAudioWithJitterCorrection(dev, track, output, progress, cancel, options);
            return;
        }

        int sectorBytes = (int)track.SectorSize;
        bool cooked = track.SectorSize == CdiSectorSize.S2048;

        // Command choice matters, and drives are strict about it:
        //  - cooked 2048 data -> READ(10). Unambiguous and universally supported.
        //    (READ CD with sector type "Any" + user-data-only is rejected by many
        //    drives: they cannot infer which bytes to strip.)
        //  - raw 2352 / audio -> READ CD, the only command that returns them.
        var expected = track.IsAudio
            ? MmcCommands.ExpectedSectorType.Cdda
            : MmcCommands.ExpectedSectorType.Any;

        var buffer = new byte[SectorsPerRead * sectorBytes];
        uint done = 0;
        var typesTried = new List<MmcCommands.ExpectedSectorType> { expected };

        while (done < track.LengthSectors)
        {
            cancel.ThrowIfCancellationRequested();

            uint chunk = Math.Min(SectorsPerRead, track.LengthSectors - done);
            var span = buffer.AsSpan(0, (int)(chunk * sectorBytes));
            uint lba = track.StartLba + done;

            var result = Issue(dev, lba, chunk, span, cooked, expected);

            if (!result.Success)
            {
                // Copy protection is not a disc fault: say so plainly and stop,
                // rather than implying damage or that a retry might help.
                if (result.Asc == Asc.CopyProtected)
                    throw new DiscReadException(
                        $"Stopped at LBA {lba} (track {track.Number}): {result.Describe()}.{Environment.NewLine}{Environment.NewLine}" +
                        "DiscForge images unencrypted discs only. It does not implement CSS " +
                        "authentication or decryption, so encrypted DVD-Video cannot be read with " +
                        "it. Everything before this point read without error.");

                // The drive rejected the request shape. Try the other sector types
                // once each before concluding anything — a mis-flagged track and a
                // fussy drive look identical from here, and both are recoverable.
                // Only away from a boundary: there this is a geometry artefact,
                // and the per-sector ladder handles it better.
                bool atBoundary = IsBoundarySector(track, lba);
                if (!cooked && IsTypeRejection(result) && !atBoundary)
                {
                    var next = FirstUntried(track.IsAudio, typesTried);
                    if (next is { } alt)
                    {
                        notes.Add($"track {track.Number}: drive rejected sector type " +
                                  $"{expected} at LBA {lba:N0} ({result.Describe()}); " +
                                  $"retrying as {alt}.");
                        expected = alt;
                        typesTried.Add(alt);
                        continue;
                    }

                    // Every whole-chunk sector type was refused. On a mixed data+audio
                    // disc (a PlayStation game, say) this is the data->audio transition:
                    // the chunk straddles the following track's pregap, where the sectors
                    // change mode partway through, so NO single type covers the whole run.
                    // Don't fail the track — drop to per-sector reads, which switch mode at
                    // the exact boundary (data sectors read as data, the pregap as audio).
                    // The original base type is restored so each sector starts from it.
                    notes.Add($"track {track.Number}: LBA {lba:N0} spans a mode transition " +
                              $"({result.Describe()}); reading it sector by sector.");
                    expected = typesTried[0];
                }

                // Otherwise it's a media error somewhere in this chunk, or we're at
                // a boundary. Narrow it down: re-read one sector at a time, so a
                // single bad sector costs one sector rather than all 27 — and retry
                // each, since marginal sectors often come back on a later attempt.
                ReadChunkSectorBySector(dev, track, output, lba, chunk, sectorBytes,
                    cooked, expected, options, badSectors, boundarySectors, notes, cancel);
            }
            else
            {
                output.Write(span);
            }

            done += chunk;
            progress?.Report(new ReadProgress(track.Number, done, track.LengthSectors,
                $"track {track.Number}: {done:N0}/{track.LengthSectors:N0} sectors"));
        }
    }

    private static MmcCommands.ExpectedSectorType? FirstUntried(
        bool isAudio, List<MmcCommands.ExpectedSectorType> tried)
    {
        foreach (var t in AlternativeTypes(isAudio, tried[0]))
            if (!tried.Contains(t))
                return t;
        return null;
    }

    /// <summary>
    /// Read an audio track using overlapping reads aligned by correlation.
    ///
    /// The subtlety: corrected output is sample-accurate, NOT sector-aligned —
    /// a +3 sample correction means the emitted stream no longer sits on 2352-byte
    /// boundaries. But the CDI track has declared exactly LengthSectors x 2352
    /// bytes, and the writer verifies that count. So the loop tracks bytes
    /// emitted, reads from wherever that lands (the overlap absorbs the
    /// remainder), and stops precisely on the declared length.
    /// </summary>
    private static void ReadAudioWithJitterCorrection(
        SptiDevice dev, ReadTrackPlan track, Stream output,
        IProgress<ReadProgress>? progress, CancellationToken cancel, ReadOptions options)
    {
        const int OverlapSectors = 2;                 // 1176 samples: ample slack
        int sectorBytes = (int)track.SectorSize;
        long expected = (long)track.LengthSectors * sectorBytes;

        var buffer = new byte[(SectorsPerRead + OverlapSectors) * sectorBytes];
        var tail = new byte[(OverlapSectors + 1) * sectorBytes];
        int tailLength = 0;
        long written = 0;
        int corrections = 0, unsure = 0;

        void Emit(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0) return;
            output.Write(data);
            written += data.Length;

            // Keep a rolling window of what we've emitted, to align against.
            if (data.Length >= tail.Length)
            {
                data[^tail.Length..].CopyTo(tail);
                tailLength = tail.Length;
            }
            else
            {
                int keep = Math.Min(tailLength, tail.Length - data.Length);
                Array.Copy(tail, tailLength - keep, tail, 0, keep);
                data.CopyTo(tail.AsSpan(keep));
                tailLength = keep + data.Length;
            }
        }

        while (written < expected)
        {
            cancel.ThrowIfCancellationRequested();

            uint sectorPos = (uint)(written / sectorBytes);
            bool first = written == 0;

            uint back = first ? 0 : Math.Min((uint)OverlapSectors, sectorPos);
            uint readLba = track.StartLba + sectorPos - back;
            uint remainingSectors = track.LengthSectors - (sectorPos - back);
            uint chunk = Math.Min((uint)(SectorsPerRead + back), remainingSectors);
            if (chunk == 0) break;

            var span = buffer.AsSpan(0, (int)(chunk * sectorBytes));

            // Retry transient failures before giving up. A cold drive — especially a
            // PATA unit on an IDE-to-USB bridge — often fails the first CD-DA reads at
            // the driver level (DeviceIoControl returns false, no SCSI sense) or reports
            // "not ready, spinning up" until the disc is up to speed. A type rejection or
            // copy-protection response is not transient, so those break out immediately
            // for the handling below.
            SptiResult result = default;
            for (int attempt = 0; attempt <= Math.Max(0, options.RetriesPerSector); attempt++)
            {
                result = Issue(dev, readLba, chunk, span, cooked: false,
                               MmcCommands.ExpectedSectorType.Cdda);
                if (result.Success || IsTypeRejection(result) || result.Asc == Asc.CopyProtected)
                    break;
                System.Threading.Thread.Sleep(30 * (attempt + 1));   // brief back-off, then re-read
            }

            if (!result.Success)
            {
                // At the lead-out the drive's read-ahead runs off the end of the
                // programme area. Emit silence for the remainder rather than
                // failing a track that is otherwise complete.
                bool nearTail = readLba + TailWindowSectors >= track.StartLba + track.LengthSectors;
                if (nearTail && options.TolerateBoundarySectors && IsTypeRejection(result))
                {
                    var silence = new byte[sectorBytes];
                    while (written < expected)
                    {
                        int take = (int)Math.Min(silence.Length, expected - written);
                        Emit(silence.AsSpan(0, take));
                    }
                    break;
                }

                throw new DiscReadException(
                    $"Read failed at LBA {readLba} (track {track.Number}): {result.Describe()}.");
            }

            if (first)
            {
                int take = (int)Math.Min(span.Length, expected - written);
                Emit(span[..take]);
            }
            else
            {
                // How much of this chunk we've already emitted: the read started
                // `back` sectors before our position, so that much is overlap.
                int overlapBytes = (int)(written - (long)(sectorPos - back) * sectorBytes);
                overlapBytes = Math.Clamp(overlapBytes, 0, Math.Min(span.Length, tailLength));

                ReadOnlySpan<byte> fresh;
                if (overlapBytes >= JitterCorrection.MinimumOverlapSamples() * JitterCorrection.BytesPerSample)
                {
                    var reference = tail.AsSpan(tailLength - overlapBytes, overlapBytes);
                    fresh = JitterCorrection.NewBytes(reference, span, overlapBytes, out var alignment);

                    if (!alignment.Confident) unsure++;
                    else if (alignment.OffsetSamples != 0) corrections++;

                    // Not confident (silence, or beyond the search window): keep
                    // the drive's own positioning rather than act on a guess.
                    if (!alignment.Confident) fresh = span[overlapBytes..];
                }
                else
                {
                    fresh = span[overlapBytes..];
                }

                if (fresh.Length == 0)
                {
                    // Can't happen with a sane overlap, but never spin forever.
                    throw new DiscReadException(
                        $"Track {track.Number}: jitter correction made no progress at LBA {readLba}.");
                }

                int take = (int)Math.Min(fresh.Length, expected - written);
                Emit(fresh[..take]);
            }

            progress?.Report(new ReadProgress(track.Number, (uint)(written / sectorBytes),
                track.LengthSectors,
                $"track {track.Number}: {written / sectorBytes:N0}/{track.LengthSectors:N0} sectors"));
        }

        if (corrections > 0 || unsure > 0)
            progress?.Report(new ReadProgress(track.Number, track.LengthSectors, track.LengthSectors,
                $"track {track.Number}: {corrections} jitter correction(s), {unsure} chunk(s) not confident"));
    }

    private static SptiResult Issue(SptiDevice dev, uint lba, uint count, Span<byte> into,
                                    bool cooked, MmcCommands.ExpectedSectorType expected)
    {
        if (cooked)
            return dev.SendCommand(MmcCommands.Read10(lba, (ushort)count), into,
                                   SptiDataDirection.In, timeoutSeconds: 60);

        // Field selection depends on the sector type, and drives enforce it:
        //  - CD-DA has NO sync, header, sub-header or EDC/ECC. Asking for them
        //    (0xF8) is an illegal field combination and is rejected outright.
        //    User Data alone (0x10) returns the full 2352 audio bytes.
        //  - A raw data sector genuinely has all of those, so 0xF8 is right.
        var fields = expected == MmcCommands.ExpectedSectorType.Cdda
            ? MmcCommands.SectorFields.UserData
            : MmcCommands.SectorFields.Raw;

        return dev.SendCommand(MmcCommands.ReadCd(lba, count, expected, fields), into,
                               SptiDataDirection.In, timeoutSeconds: 60);
    }

    /// <summary>
    /// Re-read a failed chunk one sector at a time, retrying each. Confines the
    /// damage to genuinely unreadable sectors instead of losing the whole chunk.
    /// </summary>
    private static void ReadChunkSectorBySector(
        SptiDevice dev, ReadTrackPlan track, Stream output, uint startLba, uint count,
        int sectorBytes, bool cooked, MmcCommands.ExpectedSectorType expected,
        ReadOptions options, List<uint> badSectors, List<uint> boundarySectors,
        List<string> notes, CancellationToken cancel)
    {
        var one = new byte[sectorBytes];

        for (uint i = 0; i < count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            uint lba = startLba + i;
            bool atBoundary = IsBoundarySector(track, lba);

            SptiResult last = default;
            bool got = false;

            for (int attempt = 0; attempt <= Math.Max(0, options.RetriesPerSector); attempt++)
            {
                last = Issue(dev, lba, 1, one, cooked, expected);
                if (last.Success) { got = true; break; }
                if (last.Asc == Asc.CopyProtected) break;   // protection: retrying is pointless
                if (IsTypeRejection(last)) break;           // shape rejected: the ladder handles it
            }

            // Straight re-reads exhausted. Before falling back to type/shape juggling (which
            // addresses a mis-classified track or boundary geometry, not a marginal read), try
            // harder at the SAME request shape via Tier-B adaptive re-read — the real gap this
            // fills is a plain marginal sector, away from any boundary, that isn't a type
            // rejection at all: today that sector has no escalation whatsoever between "retry
            // N times" and "give up".
            if (!got && options.AdaptiveReread && !cooked)
                got = TryAdaptiveReread(dev, track, lba, sectorBytes, one, notes);

            // Still unread. At a boundary, on a type rejection anywhere, and on ANY plain
            // failure of a cooked (DVD-mode) track, there are still request shapes worth
            // trying: for cooked tracks specifically, TryHarder's Rung 3 (READ CD asking for
            // Mode 1 user data, a different firmware path than READ(10)) is the only escalation
            // that exists at all, and until now it only ever ran at a boundary or on a type
            // rejection — never on a plain "the drive just refused this sector" failure, which
            // is exactly what a GameCube disc's non-standard sector encoding produces on an
            // otherwise normal, unmodified DVD-ROM drive (see docs/NEXT.md). Trying it here too
            // costs nothing when it doesn't help (the existing failure is reported exactly as
            // before) and can only recover sectors an unmodified drive genuinely can read.
            if (!got && (atBoundary || IsTypeRejection(last) || cooked))
            {
                got = TryHarder(dev, lba, sectorBytes, cooked, expected, track.IsAudio,
                                one, ref last);
                if (got)
                    notes.Add($"track {track.Number}: LBA {lba:N0} recovered by an " +
                              "alternative request shape.");
            }

            if (got)
            {
                output.Write(one);
                continue;
            }

            // A boundary sector that no shape will read is the drive refusing to
            // position against the pregap or lead-out — not damage. Fill and
            // record it, but don't fail an otherwise complete read over padding.
            if (atBoundary && options.TolerateBoundarySectors)
            {
                Array.Clear(one);
                output.Write(one);
                badSectors.Add(lba);
                boundarySectors.Add(lba);
                notes.Add($"track {track.Number}: LBA {lba:N0} is against a track boundary " +
                          $"and could not be positioned ({last.Describe()}); zero-filled.");
                continue;
            }

            if (!options.ContinueOnError)
                throw new DiscReadException(
                    $"Read failed at LBA {lba} (track {track.Number}) after " +
                    $"{options.RetriesPerSector + 1} attempts: {last.Describe()}.{Environment.NewLine}{Environment.NewLine}" +
                    "Try cleaning the disc (soft cloth, centre outwards). To salvage the rest, " +
                    "tick \"continue past unreadable sectors\" — the image will then be " +
                    "incomplete, and every missing sector is listed.");

            // Permitted to continue: fill the hole with zeros and record it. The
            // image is explicitly partial and the caller is told exactly where.
            Array.Clear(one);
            output.Write(one);
            badSectors.Add(lba);
        }
    }

    /// <summary>
    /// Tier B: drive one sector's read through the (already hardware-proven) adaptive re-read
    /// controller instead of the flat identical-attempt retry above — plain re-reads, then
    /// C2-assisted, then a slow C2-assisted read, stopping the moment the sector proves itself
    /// (data EDC, or full audio consensus). Only meaningful for raw 2352-byte sectors — the shape
    /// <see cref="DriveRereadSource"/> speaks — which the call site already restricts to via its
    /// own <c>!cooked</c> gate; the check here is a second, defensive guard against ever being
    /// called with anything else.
    /// </summary>
    private static bool TryAdaptiveReread(
        SptiDevice dev, ReadTrackPlan track, uint lba, int sectorBytes, byte[] one, List<string> notes)
    {
        if (sectorBytes != 2352) return false;

        var source = new DriveRereadSource(dev, lba, track.IsAudio);
        var run = AdaptiveReread.Run(source, new AdaptiveRereadConfig());
        string strategies = $"{run.StrategiesUsed} strateg{(run.StrategiesUsed == 1 ? "y" : "ies")}";

        if (run.Recovered && source.LastMain is { } main)
        {
            main.CopyTo(one, 0);
            notes.Add($"track {track.Number}: LBA {lba:N0} recovered by Tier-B adaptive re-read " +
                      $"({run.TotalReads} read(s) across {strategies}).");
            return true;
        }

        notes.Add($"track {track.Number}: LBA {lba:N0} — Tier-B adaptive re-read exhausted " +
                  $"{run.TotalReads} read(s) across {strategies}; still unreadable.");
        return false;
    }

    /// <summary>
    /// Last resort for a single sector: work through request shapes the main path
    /// doesn't use. Each rung addresses a different drive quirk, cheapest first.
    /// </summary>
    private static bool TryHarder(SptiDevice dev, uint lba, int sectorBytes, bool cooked,
                                  MmcCommands.ExpectedSectorType expected, bool isAudio,
                                  byte[] one, ref SptiResult last)
    {
        // Rung 1: the other sector types, in case this track isn't what the TOC says.
        if (!cooked)
        {
            foreach (var alt in AlternativeTypes(isAudio, expected))
            {
                last = Issue(dev, lba, 1, one, cooked, alt);
                if (last.Success) return true;
            }
        }

        // Rung 2: a batched request that begins before the boundary. Several drives
        // only apply the proximity check to single-sector requests, and will
        // happily stream the same sector as part of a run.
        if (lba >= 3)
        {
            var batch = new byte[4 * sectorBytes];
            last = Issue(dev, lba - 3, 4, batch, cooked, expected);
            if (last.Success)
            {
                Array.Copy(batch, 3 * sectorBytes, one, 0, sectorBytes);
                return true;
            }
        }

        // Rung 3, cooked only: READ CD asking for user data with an explicit
        // Mode 1 type. A different code path inside the drive's firmware from
        // READ(10), and occasionally the one that works.
        if (cooked && sectorBytes == 2048)
        {
            last = dev.SendCommand(
                MmcCommands.ReadCd(lba, 1, MmcCommands.ExpectedSectorType.Mode1,
                                   MmcCommands.SectorFields.UserData),
                one, SptiDataDirection.In, timeoutSeconds: 60);
            if (last.Success) return true;

            // Rung 4, cooked only, genuinely last resort: see the matching comment in Probe()
            // for the full reasoning. Every sector this rung applies to has already failed
            // READ(10), the alternate types above, the batched request, and Rung 3 — this is
            // only reached when nothing normal has worked. Restores the drive's error-recovery
            // settings immediately after, regardless of outcome.
            if (TryStreamingRecoveryRead(dev, lba, 1, one, cooked, expected))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Genuinely last resort for a cooked-track sector every normal request shape has already
    /// refused: temporarily tell the drive (SCSI mode page 0x01, Read-Write Error Recovery) to
    /// hand back data without insisting it passes ECC/EDC checks — Read Continuous (RC) and
    /// Disable Correction (DCR) set, Read Retry Count forced to 0 — then issue the same READ CD
    /// Mode 1 request Rung 3 already uses. This is the standard, spec-defined equivalent of the
    /// "streaming read" technique GameCube/Wii-dumping tools use to get past sector data that
    /// fails a drive's normal error correction even though it's exactly what the disc's spiral
    /// carries — see docs/NEXT.md for the real hardware report this was written for.
    ///
    /// Read-modify-write, not a hand-built page: fetches the drive's own current page first
    /// (same pattern already proven working in SptiRawDaoBurnEngine's write-parameters handling)
    /// and only touches the specific bits this needs, leaving every other field exactly as the
    /// drive reported it — a real, reported constraint on some drives/translation layers is that
    /// an unexpected value in a field this code has no reason to touch gets the whole MODE
    /// SELECT rejected.
    ///
    /// ALWAYS restores the original page byte-for-byte before returning, success or failure,
    /// via try/finally — this changes the drive's global error-recovery behavior, not just this
    /// one command, so leaving it changed would silently affect every other read this drive does
    /// for the rest of the session. If the drive doesn't support reading or writing this mode
    /// page at all, this quietly does nothing (returns false) rather than risk sending it a mode
    /// page built from nothing.
    /// </summary>
    private static bool TryStreamingRecoveryRead(SptiDevice dev, uint lba, uint count,
        Span<byte> into, bool cooked, MmcCommands.ExpectedSectorType expected)
    {
        if (!cooked) return false;

        const byte pageCode = 0x01;
        var senseBuf = new byte[64];
        var sense = dev.SendCommand(MmcCommands.ModeSense10(pageCode, (ushort)senseBuf.Length),
                                    senseBuf, SptiDataDirection.In, timeoutSeconds: 20);
        if (!sense.Success) return false;

        // MODE SENSE(10) reply: 8-byte header (bytes 6..7 = block descriptor length), that many
        // descriptor bytes, then the page itself (byte 0 = PS|page code, byte 1 = page length N,
        // N further bytes). The Read-Write Error Recovery page is 12 bytes total (2 + 10).
        int blockDescLen = (senseBuf[6] << 8) | senseBuf[7];
        int pageStart = 8 + blockDescLen;
        if (pageStart + 2 > senseBuf.Length) return false;
        int pageLen = senseBuf[pageStart + 1];
        int total = 2 + pageLen;
        if (pageLen < 4 || pageStart + total > senseBuf.Length) return false;

        var original = new byte[total];
        Array.Copy(senseBuf, pageStart, original, 0, total);

        var modified = (byte[])original.Clone();
        modified[0] &= 0x7F;                          // clear PS for MODE SELECT
        // Byte 2: AWRE(7) ARRE(6) TB(5) RC(4) EER(3) PER(2) DTE(1) DCR(0).
        // Set RC (prioritise continuous data over full recovery) and DCR (skip ECC
        // correction); clear PER so a bad-but-delivered sector isn't itself an error.
        modified[2] = (byte)((modified[2] | 0x10 | 0x01) & ~0x04);
        modified[3] = 0;                              // Read Retry Count = 0

        var setParams = MmcCommands.ModeParameterList(modified);
        var setResult = dev.SendCommand(MmcCommands.ModeSelect10((ushort)setParams.Length),
                                        setParams, SptiDataDirection.Out, timeoutSeconds: 20);
        if (!setResult.Success) return false;

        try
        {
            var result = dev.SendCommand(
                MmcCommands.ReadCd(lba, count, MmcCommands.ExpectedSectorType.Mode1,
                                   MmcCommands.SectorFields.UserData),
                into, SptiDataDirection.In, timeoutSeconds: 60);
            return result.Success;
        }
        finally
        {
            var restoreParams = MmcCommands.ModeParameterList(original);
            dev.SendCommand(MmcCommands.ModeSelect10((ushort)restoreParams.Length),
                            restoreParams, SptiDataDirection.Out, timeoutSeconds: 20);
        }
    }
}