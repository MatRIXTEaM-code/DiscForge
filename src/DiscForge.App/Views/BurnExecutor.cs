// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Windows.Forms;
using DiscForge.Core.Burning;
using DiscForge.Core.Cdi;
using DiscForge.Core.Convert;
using DiscForge.Core.Cue;
using DiscForge.Core.Devices;
using DiscForge.Core.Media;
using DiscForge.Core.Raw;
using DiscForge.Core.Reading;
using DiscForge.Devices;
using DiscForge.Devices.Burning;
using DiscForge.Devices.Media;
using DiscForge.Devices.Reading;

namespace DiscForge.App.Views;

/// <summary>
/// The burn-job planning and execution engine shared by every view that can
/// start a real burn — originally the body of <see cref="BurnView"/>, pulled
/// out so <see cref="QuickBurnView"/> (and any other simplified front end) can
/// run the exact same planner/engine path instead of reimplementing it. A view
/// still owns the UI (destination selection, actions/method/copies, the open
/// image and its shape flags) and calls into this with that state; this class
/// owns none of it beyond what a single call needs.
/// </summary>
internal sealed class BurnExecutor
{
    /// <summary>Every disc-image extension the burn engines understand, shared by
    /// every Open/Add dialog that picks a source image.</summary>
    public const string ImageFileDialogFilter =
        "Disc images (*.cdi;*.iso;*.cue;*.ccd)|*.cdi;*.iso;*.cue;*.ccd|" +
        "CDI images (*.cdi)|*.cdi|ISO images (*.iso)|*.iso|CUE sheets (*.cue)|*.cue|" +
        "CloneCD images (*.ccd)|*.ccd|All files (*.*)|*.*";

    private readonly EventLogView _log;
    private readonly ProgressBar _progress;
    private readonly Func<int?> _selectedSpeed;

    /// <param name="log">Where every planning/execution line is reported.</param>
    /// <param name="progress">The bar this engine updates as steps complete.</param>
    /// <param name="selectedSpeed">Write speed in sectors/sec, or null for the
    /// drive's default/max. Omit for a caller (like QuickBurn) with no speed
    /// picker of its own — it always burns at the drive's default.</param>
    public BurnExecutor(EventLogView log, ProgressBar progress, Func<int?>? selectedSpeed = null)
    {
        _log = log;
        _progress = progress;
        _selectedSpeed = selectedSpeed ?? (() => null);
    }

    // --- planning --------------------------------------------------------

    /// <summary>
    /// Plan a job against a single already-open image, logging its shape the
    /// same way for every source kind. Failures are logged here (refusal or
    /// exception) and reported back as null rather than thrown, so a caller
    /// can treat "couldn't plan this one" uniformly — log it, stop — without
    /// duplicating the try/catch/log dance.
    /// </summary>
    public static MultiBurnPlan? PlanImage(string openCdi, bool sourceIsIso, bool sourceIsCue,
                                            MultiBurnJob job, EventLogView log)
    {
        try
        {
            ImageShape shape;
            if (sourceIsIso)
            {
                // A plain ISO is by definition one Mode 1 data track, one session,
                // no audio — its shape is known without parsing anything, and
                // parsing it as a CDI would (correctly) fail: it has no trailer.
                long size = new FileInfo(openCdi).Length;
                shape = new ImageShape(TrackCount: 1, SessionCount: 1,
                                       HasAudio: false, HasData: true, NonStandardGaps: false);
                log.Add($"Image: ISO, {size / 2048:N0} sectors, {size / (1024.0 * 1024.0):N1} MB");
            }
            else if (sourceIsCue)
            {
                // A CUE sheet is a demand for EXACT layout — that's what the
                // format is for — so its shape declares non-standard gaps and
                // the planner routes it to RAW DAO. TAO would silently rewrite
                // the very things the sheet specifies.
                var cue = CueSheet.Parse(File.ReadAllText(openCdi));
                bool audio = cue.Tracks.Any(t => t.Type == CueTrackType.Audio);
                bool data = cue.Tracks.Any(t => t.Type != CueTrackType.Audio);
                shape = new ImageShape(cue.Tracks.Count, SessionCount: 1,
                                       HasAudio: audio, HasData: data, NonStandardGaps: true);
                log.Add($"Image: CUE, {cue.Tracks.Count} track(s), exact layout (RAW DAO)");
            }
            else
            {
                using var fs = File.OpenRead(openCdi);
                var image = CdiParser.Parse(fs);
                shape = ImageShape.Of(image);
                log.Add($"Image: {image.TrackCount} track(s), {image.Sessions.Count} session(s)");
            }

            return BurnJobPlanner.PlanAll(shape, job);
        }
        catch (BurnNotSupportedException ex)
        {
            log.Add("Job refused: " + ex.Message, EventLogView.Level.Error);
            return null;
        }
        catch (Exception ex)
        {
            log.Add("Error: " + ex.Message, EventLogView.Level.Error);
            AppLog.WriteException("burn plan", ex);
            return null;
        }
    }

    /// <summary>Log a plan's per-destination detail and, for disc destinations,
    /// confirm media is loaded before anything is touched. False means the
    /// caller should not proceed (nothing runnable, or the user declined).</summary>
    public static bool LogPlanAndConfirm(MultiBurnPlan plan, EventLogView log, string mediaPromptSuffix = "")
    {
        foreach (var d in plan.Refused)
            log.Add($"{d.Label}: SKIPPED — {d.Refusal}", EventLogView.Level.Error);

        foreach (var d in plan.Runnable)
        {
            log.Add($"Destination: {d.Label}");
            foreach (var w in d.Warnings) log.Add($"  {w}", EventLogView.Level.Warn);
            foreach (var st in d.Steps)
                log.Add($"  planned: {st.Kind} via {st.Method}" +
                        (d.TotalCopies > 1 ? $" (copy {st.CopyNumber}/{d.TotalCopies})" : ""));
        }

        int discs = plan.Runnable.Count(d => !d.IsImageFile);
        if (discs == 0) return true;

        var prompt = (discs == 1
            ? "Insert media. Begin the job?"
            : $"Insert blank media in all {discs} drives. They will be burned simultaneously. Begin?")
            + mediaPromptSuffix;
        if (RetroMessageBox.Show(prompt, "DiscForge",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
        {
            log.Add("Cancelled.", EventLogView.Level.Warn);
            return false;
        }
        return true;
    }

    // --- execution ---------------------------------------------------------

    /// <summary>
    /// Run every runnable destination in <paramref name="plan"/> CONCURRENTLY —
    /// burning several drives at once is the point of a multi-destination job
    /// (for a single-drive QuickBurn plan, that's simply one task). Each gets
    /// its own engine and its own handle on the source image (they must not
    /// share a Stream), and one failing doesn't abort the others: you get a
    /// per-destination verdict.
    /// </summary>
    public async Task RunAllAsync(MultiBurnPlan plan, string openCdi, bool sourceIsIso, bool sourceIsCue)
    {
        var targets = plan.Runnable.ToList();
        var fractions = new double[targets.Count];

        var tasks = targets.Select((d, index) =>
        {
            // Progress<T> captures the SynchronizationContext where it is BUILT.
            // Constructing it inside Task.Run would leave it with none, and its
            // callback would then touch the progress bar from a worker thread.
            // Build it here, on the UI thread; run the work below.
            // The bar shows progress; the log tells the story. Logging every
            // report turned a 2 GB verify into 30,000+ lines and made the
            // diagnostics useless.
            string lastPhase = "";
            var progress = new Progress<BurnProgress>(p =>
            {
                fractions[index] = p.Fraction;
                // Overall progress is the mean across destinations; they run at
                // their own speeds.
                _progress.Value = Math.Clamp((int)(fractions.Average() * 100), 0, 100);

                if (!string.IsNullOrEmpty(p.Detail) && p.Phase != lastPhase)
                {
                    lastPhase = p.Phase;
                    _log.Add($"[{Short(d.Label)}] {p.Phase}: {p.Detail}");
                }
                else
                {
                    StatusBus.Report($"{Short(d.Label)} {p.Phase}: {p.Detail}");
                }
            });

            return Task.Run(async () =>
            {
                try
                {
                    foreach (var step in d.Steps)
                    {
                        _log.Add($"[{Short(d.Label)}] {step.Kind}" +
                                 (d.TotalCopies > 1 ? $" (copy {step.CopyNumber}/{d.TotalCopies})" : "") + "…");

                        if (d.IsImageFile)
                            await RunFileStepAsync(step, ((BurnDestination.ImageFile)d.Destination).Path,
                                openCdi, sourceIsIso, sourceIsCue);
                        else
                            await RunDriveStepAsync(step, ((BurnDestination.Drive)d.Destination).Capabilities,
                                progress, openCdi, sourceIsIso, sourceIsCue);

                        _log.Add($"[{Short(d.Label)}] {step.Kind} completed.", EventLogView.Level.Good);
                    }
                    fractions[index] = 1.0;
                    return (d, Ok: true, Error: (string?)null);
                }
                catch (NotImplementedException ex)
                {
                    _log.Add($"[{Short(d.Label)}] unavailable: {ex.Message}", EventLogView.Level.Error);
                    return (d, Ok: false, Error: ex.Message);
                }
                catch (Exception ex)
                {
                    _log.Add($"[{Short(d.Label)}] failed: {ex.Message}", EventLogView.Level.Error);
                    AppLog.WriteException($"burn to {d.Label}", ex);
                    return (d, Ok: false, Error: ex.Message);
                }
            });
        }).ToList();

        var results = await Task.WhenAll(tasks);

        int good = results.Count(r => r.Ok);
        int bad = results.Length - good;
        _progress.Value = 100;

        if (bad == 0)
            _log.Add($"Job complete: {good} destination(s) succeeded.", EventLogView.Level.Good);
        else
            _log.Add($"Job finished: {good} succeeded, {bad} FAILED.",
                bad == results.Length ? EventLogView.Level.Error : EventLogView.Level.Warn);
    }

    private async Task RunDriveStepAsync(BurnStep step, DriveCapabilities drive, IProgress<BurnProgress> progress,
                                          string openCdi, bool sourceIsIso, bool sourceIsCue)
    {
        // Each step kind is a different operation. Treating them alike meant
        // Verify called the burn engine — i.e. it would have re-burned the disc.
        switch (step.Kind)
        {
            case BurnStepKind.Verify:
                if (sourceIsCue)
                    await VerifyRawDiscAsync(drive, openCdi);
                else
                    await VerifyDiscAsync(drive, progress, openCdi, sourceIsIso);
                return;

            case BurnStepKind.Test:
                if (sourceIsCue)
                {
                    await TestRawDiscAsync(drive, openCdi);
                    return;
                }
                // IMAPI2's data path exposes no simulated burn, and pretending
                // otherwise would be worse than saying so.
                throw new NotImplementedException(
                    "Test (simulated burn) isn't available through the IMAPI2 data path for ISO/CDI " +
                    "sources. Untick Test, or use a rewritable disc so a real write costs nothing. " +
                    "(A RAW/CUE source DOES support a laser-off Test.)");

            case BurnStepKind.Write:
                break;

            default:
                throw new NotSupportedException($"Unknown step {step.Kind}.");
        }

        var burnPlan = new BurnPlan
        {
            Method = step.Method,
            DevicePath = drive.DevicePath,   // the real path, not the display label
            Warnings = Array.Empty<string>(),
            WriteSpeedSectorsPerSecond = _selectedSpeed(),
        };

        // An ISO goes straight to the burner: it already IS the cooked data, so
        // there's nothing to extract and no staging copy to wait for.
        if (sourceIsIso)
        {
            await Task.Run(() => new Imapi2BurnEngine().BurnIso(openCdi, burnPlan, progress));
            return;
        }

        // A CUE sheet carries the full layout; the RAW engine composes and
        // writes the whole disc from it.
        if (sourceIsCue)
        {
            await Task.Run(() =>
            {
                using var layout = DiscLayout.FromCueFile(openCdi);
                new RawDaoBurnEngine().BurnLayout(layout, burnPlan, progress);
            });
            return;
        }

        IBurnEngine engine = step.Method switch
        {
            BurnMethod.Imapi2Data => new Imapi2BurnEngine(),
            BurnMethod.Imapi2TrackAtOnce => new Imapi2TrackAtOnceBurnEngine(),
            _ => new RawDaoBurnEngine(),
        };

        await Task.Run(() =>
        {
            using var fs = File.OpenRead(openCdi);
            var img = CdiParser.Parse(fs);
            engine.Burn(fs, img, burnPlan, progress);
        });
    }

    /// <summary>
    /// Verify a burn by reading the disc back and comparing it against the source,
    /// sector for sector. This is what Verify always should have been: the burn
    /// engines write, so calling one here re-burned the disc instead of checking it.
    /// </summary>
    private async Task VerifyDiscAsync(DriveCapabilities drive, IProgress<BurnProgress> progress,
                                        string openCdi, bool sourceIsIso)
    {
        var letter = DriveLetterOf(drive)
            ?? throw new InvalidOperationException($"No drive letter in '{drive.DevicePath}'.");

        await Task.Run(() =>
        {
            // Read the disc's own TOC rather than assuming it matches the source:
            // if the burn went wrong, the difference is exactly what we're after.
            var toc = DiscReader.ReadToc(letter);
            var plan = ReadPlanner.Plan(toc, drive);

            var track = plan.Tracks.FirstOrDefault(t => !t.IsAudio)
                ?? throw new InvalidDataException("The disc has no data track to verify.");

            using var source = OpenSourceUserData(openCdi, sourceIsIso);
            long expected = source.Length;
            long onDisc = (long)track.LengthSectors * (int)track.SectorSize;

            // A zero-length source proves nothing: comparing 0 bytes would "match" vacuously. Verify must
            // never report success without having compared real data — refuse rather than pass empty.
            if (expected <= 0)
                throw new InvalidDataException(
                    "The source image is empty — there is nothing to verify against. Refusing to report a pass.");

            if (onDisc < expected)
                throw new InvalidDataException(
                    $"The disc holds {onDisc:N0} bytes but the image is {expected:N0} — " +
                    "the burn is short.");

            // A burner may pad the last few sectors; compare only what we wrote.
            using var dev = new DiscSectorStream(letter, track.StartLba, (int)track.SectorSize);
            long same = CompareStreams(source, dev, expected, progress);

            if (same != expected)
                throw new InvalidDataException(
                    $"The disc differs from the image at byte {same:N0}.");
        });

        _log.Add("Verify: the disc matches the image byte for byte.", EventLogView.Level.Good);
    }

    /// <summary>
    /// Verify a RAW (CUE) burn: rebuild the golden RAW image from the cue — the exact bytes the
    /// RAW-DAO engine wrote, sub-channel included — then read the disc back a track at a time and
    /// compare each against the golden. Read-backs take a sub-channel consensus (several passes,
    /// majority-voted Q) so a transient one-sector Q mis-read can't fail a byte-faithful burn. This
    /// is the raw counterpart to <see cref="VerifyDiscAsync"/>, which only handles cooked ISO/CDI
    /// data tracks and can't see the sub-channel a RAW-DAO burn exists to get right.
    /// </summary>
    private async Task VerifyRawDiscAsync(DriveCapabilities drive, string openCdi)
    {
        var letter = DriveLetterOf(drive)
            ?? throw new InvalidOperationException($"No drive letter in '{drive.DevicePath}'.");

        await Task.Run(() =>
        {
            using var layout = DiscLayout.FromCueFile(openCdi);
            string goldenPath = Path.Combine(Path.GetTempPath(), $"dforge-golden-{Guid.NewGuid():N}.img");
            try
            {
                using (var gs = File.Create(goldenPath))
                    RawImageGenerator.Generate(layout, RawSubcodeForm.Interleaved96, gs);

                using var dev = new DiscForge.Devices.Spti.SptiDevice(letter);
                var toc = DiscReader.ReadToc(dev);

                int passed = 0, total = 0;
                foreach (var t in toc.Tracks)
                {
                    total++;
                    var field = t.IsData
                        ? RawDiscReader.FieldSelect.Data
                        : RawDiscReader.FieldSelect.Audio;

                    using var readback = new MemoryStream();
                    RawDiscReader.ReadConsensus(dev, (int)t.StartLba, t.LengthSectors, readback,
                        passes: 3, progress: null, field, out var crep);

                    readback.Position = 0;
                    using var golden = File.OpenRead(goldenPath);
                    var rep = RawReadbackCompare.Compare(golden, readback, partial: true);

                    var level = rep.Result == RawReadbackCompare.Grade.Fail ? EventLogView.Level.Error
                              : rep.Result == RawReadbackCompare.Grade.PassWithNotes ? EventLogView.Level.Warn
                              : EventLogView.Level.Good;
                    _log.Add($"[{Short(drive.DevicePath)}] track {t.Number} ({(t.IsData ? "data" : "audio")}): {rep.Summary}"
                             + (crep.SubCorrected > 0 ? $"  [consensus out-voted {crep.SubCorrected} sub-channel Q]" : ""),
                             level);
                    if (rep.Result != RawReadbackCompare.Grade.Fail) passed++;
                }

                if (total == 0)
                    throw new InvalidDataException("The disc reports no tracks to verify.");
                if (passed != total)
                    throw new InvalidDataException(
                        $"Raw verify: {passed}/{total} track(s) passed — the burn is not byte-faithful.");
            }
            finally
            {
                try { File.Delete(goldenPath); } catch { /* best-effort temp cleanup */ }
            }
        });

        _log.Add("Verify: the disc matches the golden RAW image byte for byte (main channel + sub-channel).",
                 EventLogView.Level.Good);
    }

    /// <summary>
    /// Test a RAW (CUE) burn without writing: the direct-SPTI engine runs the whole raw-DAO
    /// addressing/sequence against the drive with the laser OFF (its non-destructive
    /// <see cref="SptiRawDaoBurnEngine.TestCue"/>). Unlike the IMAPI2 data path, the raw path really
    /// can simulate, so Test is meaningful — and free — for CUE sources.
    /// </summary>
    private async Task TestRawDiscAsync(DriveCapabilities drive, string openCdi)
    {
        var letter = DriveLetterOf(drive)
            ?? throw new InvalidOperationException($"No drive letter in '{drive.DevicePath}'.");

        var result = await Task.Run(() =>
        {
            using var layout = DiscLayout.FromCueFile(openCdi);
            return SptiRawDaoBurnEngine.TestCue(letter, layout);
        });

        if (!result.Accepted)
            throw new InvalidDataException($"Test (laser-off) rejected by the drive: {result.Detail}");
        _log.Add($"Test (laser-off) passed: {result.Detail} " +
                 $"({result.CueEntries} cue entries, {result.CueBytes:N0} bytes) — no disc written.",
                 EventLogView.Level.Good);
    }

    /// <summary>Image-file destination: Write copies the image; Verify compares
    /// the copy against the source with CdiComparer (structure + per-track CRC).
    /// Works with no hardware at all.</summary>
    public async Task RunFileStepAsync(BurnStep step, string destPath, string openCdi, bool sourceIsIso, bool sourceIsCue)
    {
        if (sourceIsCue)
            throw new NotSupportedException(
                "A CUE sheet describes a disc, not a single image file. Burn it to a " +
                "drive, or use the CLI's build-raw command to generate a raw image file.");

        if (step.Kind == BurnStepKind.Write)
        {
            // An ISO destined for a .cdi file must be wrapped, not just copied —
            // otherwise the result has no descriptor and isn't a CDI at all.
            if (sourceIsIso)
            {
                await Task.Run(() =>
                {
                    using var os = File.Create(destPath);
                    IsoConverter.IsoToCdi(openCdi, CdiVersion.V35, os);
                });
                _log.Add($"Wrapped the ISO into {Path.GetFileName(destPath)}");
            }
            else
            {
                await Task.Run(() => File.Copy(openCdi, destPath, overwrite: true));
                _log.Add($"Wrote {Path.GetFileName(destPath)}");
            }
            return;
        }

        if (step.Kind == BurnStepKind.Verify)
        {
            if (sourceIsIso)
            {
                // Compare the wrapped copy's data track against the source ISO.
                var same = await Task.Run(() =>
                {
                    using var b = File.OpenRead(destPath);
                    var ib = CdiParser.Parse(b);
                    using var extracted = new MemoryStream();
                    using var source = File.OpenRead(openCdi);
                    if (source.Length == 0)
                        throw new InvalidDataException("The source ISO is empty — refusing to report a vacuous match.");
                    var track = ib.AllTracks.Single();
                    using var view = new CdiUserDataStream(b, track);
                    return StreamsEqual(source, view);
                });
                if (same) { _log.Add("Verify: the wrapped image matches the source ISO.", EventLogView.Level.Good); return; }
                throw new InvalidDataException("Verify failed: the wrapped image differs from the ISO.");
            }

            var report = await Task.Run(() =>
            {
                using var a = File.OpenRead(openCdi);
                using var b = File.OpenRead(destPath);
                var ia = CdiParser.Parse(a);
                var ib = CdiParser.Parse(b);
                return CdiComparer.Compare(a, ia, b, ib);
            });

            if (report.Equal)
            {
                _log.Add("Verify: images are equivalent (structure + CRC-32).", EventLogView.Level.Good);
                return;
            }

            foreach (var s in report.StructuralDifferences) _log.Add("  " + s, EventLogView.Level.Error);
            foreach (var t in report.TrackDifferences)
                _log.Add($"  track {t.TrackNumber} {t.Field}: {t.ValueA} vs {t.ValueB}", EventLogView.Level.Error);
            foreach (var n in report.ContentMismatchTracks)
                _log.Add($"  track {n} content differs", EventLogView.Level.Error);
            throw new InvalidDataException("Verify failed: images differ.");
        }
    }

    /// <summary>The source's cooked user data, whether it's an ISO or a CDI.</summary>
    private static Stream OpenSourceUserData(string openCdi, bool sourceIsIso)
    {
        if (sourceIsIso) return File.OpenRead(openCdi);

        var fs = File.OpenRead(openCdi);
        var image = CdiParser.Parse(fs);
        var track = image.AllTracks.First(t => t.Mode != CdiTrackMode.Audio);
        return new CdiUserDataStream(fs, track);
    }

    /// <summary>Byte-compare two streams without holding either in memory.</summary>
    private static bool StreamsEqual(Stream a, Stream b)
    {
        var ba = new byte[64 * 1024];
        var bb = new byte[64 * 1024];
        while (true)
        {
            int na = a.ReadAtLeast(ba, ba.Length, throwOnEndOfStream: false);
            int nb = b.ReadAtLeast(bb, bb.Length, throwOnEndOfStream: false);
            if (na != nb) return false;
            if (na == 0) return true;
            if (!ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
        }
    }

    /// <summary>Compare two streams, reporting progress. Returns bytes matched.</summary>
    private static long CompareStreams(Stream a, Stream b, long length, IProgress<BurnProgress> progress)
    {
        var ba = new byte[64 * 1024];
        var bb = new byte[64 * 1024];
        long done = 0;
        long lastReported = 0;

        while (done < length)
        {
            int want = (int)Math.Min(ba.Length, length - done);
            int na = a.ReadAtLeast(ba, want, throwOnEndOfStream: false);
            int nb = b.ReadAtLeast(bb, want, throwOnEndOfStream: false);
            if (na == 0 || nb == 0) break;

            int n = Math.Min(na, nb);
            for (int i = 0; i < n; i++)
                if (ba[i] != bb[i]) return done + i;

            done += n;

            // Report about every 32 MB, not every 64 KB block: a 2 GB verify
            // otherwise fires 32,768 progress reports, and each one used to
            // become a log line.
            if (done - lastReported >= 32L * 1024 * 1024 || done == length)
            {
                lastReported = done;
                progress.Report(new BurnProgress("verify", done / (double)length,
                    $"verified {done / (1024.0 * 1024.0):N0} MB of {length / (1024.0 * 1024.0):N0}"));
            }
        }
        return done;
    }

    /// <summary>Short label for log lines — the device path or file name.</summary>
    public static string Short(string label)
    {
        int open = label.LastIndexOf('(');
        if (open > 0 && label.EndsWith(')'))
            return label[(open + 1)..^1];
        return Path.GetFileName(label);
    }

    /// <summary>The drive letter implied by a device path (e.g. \\.\E: → 'E'), or
    /// null if the path doesn't carry one.</summary>
    public static char? DriveLetterOf(DriveCapabilities drive)
    {
        var path = drive.DevicePath;                 // \\.\E:
        int i = path.LastIndexOf(':');
        return i > 0 ? path[i - 1] : null;
    }
}
