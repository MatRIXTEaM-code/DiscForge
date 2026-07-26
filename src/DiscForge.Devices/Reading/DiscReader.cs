// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Runtime.Versioning;
using DiscForge.Core.Audio;
using DiscForge.Core.Cdi;
using DiscForge.Core.Devices;
using DiscForge.Core.Mmc;
using DiscForge.Core.Reading;
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

    private static void ReadTrack(SptiDevice dev, ReadTrackPlan track, Stream output,
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

                    throw new DiscReadException(
                        $"Read failed at LBA {lba} (track {track.Number}): {result.Describe()}." +
                        $"{Environment.NewLine}{Environment.NewLine}" +
                        $"The drive refused every sector type for this track " +
                        $"({string.Join(", ", typesTried)}). If this is a mixed-mode or " +
                        "multi-session disc, try the other raw/cooked setting.");
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
            var result = Issue(dev, readLba, chunk, span, cooked: false,
                               MmcCommands.ExpectedSectorType.Cdda);

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

            // Straight re-reads exhausted. At a boundary, and on a type rejection
            // anywhere, there are still request shapes worth trying.
            if (!got && (atBoundary || IsTypeRejection(last)))
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
        }

        return false;
    }
}