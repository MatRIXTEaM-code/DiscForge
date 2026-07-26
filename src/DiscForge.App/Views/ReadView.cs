// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Cdi;
using DiscForge.Core.Devices;
using DiscForge.Core.Mmc;
using DiscForge.Core.Reading;
using DiscForge.Devices;
using DiscForge.Devices.Reading;

namespace DiscForge.App.Views;

/// <summary>
/// Read a disc to a CDI image — the other half of a disc tool. Pick a drive,
/// read its TOC, review the plan, then rip.
///
/// The decisions (what sector size per track, whether raw is forced) come from
/// the pure ReadPlanner in Core; this view collects the request and reports.
/// Sector modes are probed from the disc once per TOC read and handed to the
/// planner, because the TOC alone cannot distinguish Mode 1 from Mode 2 — and
/// that difference decides whether a cooked read is even possible.
/// </summary>
internal sealed class ReadView : UserControl
{
    private readonly ComboBox _drives = new()
    {
        Width = 300, DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Ui,
        Location = new Point(70, 13),
    };
    private readonly CheckBox _raw = new()
    {
        Text = "Read data tracks raw (2352 bytes/sector)", AutoSize = true,
        Location = new Point(12, 48), Font = Theme.Ui,
    };
    private readonly CheckBox _continueOnError = new()
    {
        Text = "Continue past unreadable sectors (image will be incomplete)", AutoSize = true,
        Location = new Point(12, 70), Font = Theme.Ui,
    };
    private readonly CheckBox _jitter = new()
    {
        Text = "Correct audio jitter (slower, more accurate)", AutoSize = true,
        Location = new Point(12, 92), Font = Theme.Ui, Enabled = false,
    };
private readonly CheckBox _captureSub = new()
    {
        Text = "Also capture sub-channel to a .sub sidecar", AutoSize = true,
        Location = new Point(12, 114), Font = Theme.Ui,
    };
    private readonly ListView _tracks = new()
    {
        // Sits below the four option checkboxes (the last ends near Y=133); a
        // grid any higher overlaps the "capture sub-channel" row.
        Location = new Point(12, 140), Size = new Size(712, 100),
        View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        Font = Theme.Ui, BackColor = Color.White,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly Button _rip = new()
    {
        Text = "Read to CDI…", Location = new Point(12, 250), Width = 110, Height = 28,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly ProgressBar _progress = new()
    {
        Location = new Point(132, 253), Size = new Size(592, 22), Minimum = 0, Maximum = 100,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly EventLogView _log = new()
    {
        Location = new Point(12, 286), Size = new Size(712, 170),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
    };

    private IReadOnlyList<DriveCapabilities> _detected = Array.Empty<DriveCapabilities>();
    private DiscToc? _toc;
    private bool _suppressPlanRebuild;
    private ReadPlan? _plan;

    /// <summary>What each track's sectors actually are, probed once per TOC read.
    /// Cached because toggling a checkbox shouldn't send the drive back to the
    /// disc — and because the answer cannot change without the disc changing.</summary>
    private IReadOnlyDictionary<int, TrackSectorMode>? _modes;

    public ReadView()
    {
        // Establish a realistic size before adding anchored children (see InspectView).
        Size = new Size(736, 470);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Drive:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });

        var detect = new Button { Text = "Detect", Location = new Point(378, 12), Width = 74, FlatStyle = FlatStyle.System };
        detect.Click += async (_, _) => await DetectAsync();

        var readToc = new Button { Text = "Read TOC", Location = new Point(458, 12), Width = 84, FlatStyle = FlatStyle.System };
        readToc.Click += async (_, _) => await ReadTocAsync();

        _raw.CheckedChanged += (_, _) => { if (_toc is not null && !_suppressPlanRebuild) BuildPlan(); };

        foreach (var (name, w) in new[]
        {
            ("Track", 60), ("Type", 130), ("Start LBA", 90), ("Sectors", 100), ("Size", 100), ("Stored", 120),
        })
            _tracks.Columns.Add(name, w);

        _rip.Click += async (_, _) => await RipAsync();

        Controls.Add(_drives); Controls.Add(detect); Controls.Add(readToc);
        Controls.Add(_raw);
        Controls.Add(_continueOnError);
        Controls.Add(_jitter);
        Controls.Add(_captureSub);
        Controls.Add(_tracks);
        Controls.Add(_rip); Controls.Add(_progress);
        Controls.Add(_log);

        _log.Add("Detect a drive, insert a disc, then read its TOC.");
    }

    private async Task DetectAsync()
    {
        _drives.Items.Clear();
        _log.Add("Detecting drives…");
        try
        {
            _detected = await Task.Run(() => DriveDetector.DetectAll());
            foreach (var d in _detected)
            {
                _drives.Items.Add(d.Summary());
                // Record the full capability picture: it's the first thing needed
                // to diagnose a read that misbehaves.
                AppLog.Write($"  drive {d.DevicePath}: '{d.Vendor}' '{d.Model}' fw '{d.FirmwareRevision}' " +
                             $"CD r/w={d.CdRead}/{d.CdWrite} DVD r/w={d.DvdRead}/{d.DvdWrite} " +
                             $"BD r/w={d.BdRead}/{d.BdWrite} rawDAO96={d.RawDao96} media={d.MediaProfile}");
            }
            if (_drives.Items.Count > 0)
            {
                _drives.SelectedIndex = 0;
                _log.Add($"{_detected.Count} drive(s) detected.", EventLogView.Level.Good);
            }
            else
            {
                _log.Add("No optical drives detected (raw access usually needs administrator).",
                    EventLogView.Level.Warn);
            }
        }
        catch (Exception ex)
        {
            _log.Add("Detection failed: " + ex.Message, EventLogView.Level.Error);
        }
    }

    private char? SelectedDriveLetter()
    {
        if (_drives.SelectedIndex < 0 || _drives.SelectedIndex >= _detected.Count) return null;
        var path = _detected[_drives.SelectedIndex].DevicePath;   // e.g. \\.\D:
        var idx = path.LastIndexOf(':');
        return idx > 0 ? path[idx - 1] : null;
    }

    private async Task ReadTocAsync()
    {
        var letter = SelectedDriveLetter();
        if (letter is null) { _log.Add("Detect and select a drive first.", EventLogView.Level.Warn); return; }

        _tracks.Items.Clear();
        _rip.Enabled = false;
        _modes = null;
        _log.Add($"Reading TOC from {letter}:…");
        try
        {
            _toc = await Task.Run(() => DiscReader.ReadToc(letter.Value));
            var media = _detected[_drives.SelectedIndex];
            _log.Add($"Media: {media.MediaProfile}");
            _log.Add($"TOC: tracks {_toc.FirstTrack}–{_toc.LastTrack}, lead-out at LBA {_toc.LeadOutLba:N0}.",
                EventLogView.Level.Good);

            // Ask the disc what its sectors actually are. The TOC's control nibble
            // separates audio from data and nothing more, so without this a Mode 2
            // Form 2 track — SVCD, VCD, CD-i — looks identical to plain Mode 1 and
            // gets planned as a cooked read the drive will refuse outright.
            if (!media.MediaIsDvdOrBd)
            {
                var toc = _toc;
                _modes = await Task.Run(() => TrackModeProber.Probe(letter.Value, toc));
                foreach (var kv in _modes)
                    AppLog.Write($"    probe: track {kv.Key} sectors are {kv.Value}");
            }

            bool anyForm2 = _modes is not null &&
                            _modes.Values.Any(m => m == TrackSectorMode.Mode2Form2);

            // Raw 2352 sectors are a CD concept; DVD/BD have no raw form.
            // Adjusting the checkbox here fires CheckedChanged, whose handler
            // also calls BuildPlan — suppress it so the plan is built (and
            // logged) exactly once per TOC read.
            _suppressPlanRebuild = true;
            try
            {
                if (media.MediaIsDvdOrBd)
                {
                    _raw.Checked = false;
                    _raw.Enabled = false;
                    _raw.Text = "Read data tracks raw (CD only — not available for DVD/BD)";
                }
                else if (anyForm2 || _toc.HasAudio)
                {
                    // Not a preference on this disc: there is no 2048-byte user
                    // data to read. Tick it, lock it, and say why.
                    _raw.Checked = true;
                    _raw.Enabled = false;
                    _raw.Text = anyForm2
                        ? "Read data tracks raw (required — disc has Mode 2 Form 2 tracks)"
                        : "Read data tracks raw (required — disc has audio tracks)";
                }
                else
                {
                    _raw.Enabled = true;
                    _raw.Text = "Read data tracks raw (2352 bytes/sector)";
                }
            }
            finally { _suppressPlanRebuild = false; }

            if (_toc.IsMixedMode) _log.Add("Mixed-mode disc (audio + data).", EventLogView.Level.Warn);
            BuildPlan();
        }
        catch (Exception ex)
        {
            _toc = null;
            _modes = null;
            _log.Add("Read TOC failed: " + ex.Message, EventLogView.Level.Error);
        }
    }

    /// <summary>Human label for the Type column: what the track is, and — where it
    /// differs from the obvious — what its sectors turned out to be.</summary>
    private static string DescribeType(ReadTrackPlan t) => t.IsAudio
        ? "Audio"
        : t.Detected switch
        {
            TrackSectorMode.Mode1 => "Data (Mode 1)",
            TrackSectorMode.Mode2Form1 => "Data (Mode 2 Form 1)",
            TrackSectorMode.Mode2Form2 => "Data (Mode 2 Form 2)",
            _ => "Data",
        };

    private void BuildPlan()
    {
        if (_toc is null) return;
        var drive = _detected[_drives.SelectedIndex];
        _tracks.Items.Clear();
        try
        {
            _plan = ReadPlanner.Plan(_toc, drive, _raw.Checked, _modes);
            AppLog.Write($"  plan: raw={_plan.RawMode} rawRequested={_raw.Checked} " +
                         $"rawRequired={_plan.RawRequired} " +
                         $"tracks={_plan.Tracks.Count} totalBytes={_plan.TotalBytes:N0}");
            foreach (var t in _plan.Tracks)
                AppLog.Write($"    track {t.Number}: {(t.IsAudio ? "audio" : "data")} " +
                             $"lba={t.StartLba} sectors={t.LengthSectors} " +
                             $"sectorSize={(int)t.SectorSize} mode={t.Mode} detected={t.Detected}");
            foreach (var t in _plan.Tracks)
            {
                var item = new ListViewItem(t.Number.ToString());
                item.SubItems.Add(DescribeType(t));
                item.SubItems.Add(t.StartLba.ToString("N0"));
                item.SubItems.Add(t.LengthSectors.ToString("N0"));
                item.SubItems.Add($"{(int)t.SectorSize} b/sector");
                item.SubItems.Add($"{t.StoredBytes / (1024.0 * 1024.0):N1} MB");
                _tracks.Items.Add(item);
            }
            bool anyAudio = _plan.Tracks.Any(t => t.IsAudio);
            _jitter.Enabled = anyAudio;
            if (!anyAudio)
            {
                _jitter.Checked = false;
                _jitter.Text = "Correct audio jitter (audio discs only)";
            }
            else
            {
                _jitter.Text = "Correct audio jitter (slower, more accurate)";
            }

            foreach (var w in _plan.Warnings) _log.Add(w, EventLogView.Level.Warn);
            _log.Add($"Plan: {_plan.Tracks.Count} track(s), {_plan.TotalBytes / (1024.0 * 1024.0):N1} MB total" +
                     (_plan.RawMode ? " (raw)" : " (cooked)"));
            _rip.Enabled = true;
        }
        catch (ReadNotSupportedException ex)
        {
            _plan = null;
            _log.Add("Cannot read this disc: " + ex.Message, EventLogView.Level.Error);
        }
    }

    private async Task RipAsync()
    {
        var letter = SelectedDriveLetter();
        if (letter is null || _plan is null) return;

        // Pre-flight: try one sector of each track. Finding out the drive can't
        // honour the plan takes a moment now, versus a failed part-file later.
        _log.Add("Testing the drive can read this disc as planned…");
        var problem = await Task.Run(() => DiscReader.Probe(letter.Value, _plan));
        if (problem is not null)
        {
            _log.Add(problem, EventLogView.Level.Error);
            return;
        }
        _log.Add("Test read OK.", EventLogView.Level.Good);

        using var save = new SaveFileDialog { Filter = "CDI image (*.cdi)|*.cdi", FileName = "disc.cdi" };
        if (save.ShowDialog() != DialogResult.OK) return;

        _rip.Enabled = false;
        _progress.Value = 0;
        _log.Add($"Reading to {Path.GetFileName(save.FileName)}…");

        // A DVD-Shrink-style window carries the live readout — stage, transfer
        // rate, sectors done and time remaining — and its Cancel stops the read.
        var dlg = new RipProgressDialog($"Reading {Path.GetFileName(save.FileName)}");
        dlg.FormClosed += (_, _) => dlg.Dispose();
        using var cts = new CancellationTokenSource();
        dlg.CancelRequested += () => cts.Cancel();
        dlg.Show(this);                 // create the window (and its handle) first
        dlg.SetTitleBase("Reading disc");
        bool ok = false;

        // Progress is reported per track, so map each track number to how many
        // sectors precede it — otherwise the bar restarts on every track.
        var offsets = new Dictionary<int, uint>();
        uint acc = 0;
        foreach (var t in _plan.Tracks) { offsets[t.Number] = acc; acc += t.LengthSectors; }
        uint totalSectors = Math.Max(1u, acc);
        double totalBytes = Math.Max(1.0, _plan.TotalBytes);
        long startTick = Environment.TickCount64;

        var progress = new Progress<ReadProgress>(p =>
        {
            uint overall = (offsets.TryGetValue(p.TrackNumber, out var before) ? before : 0) + p.SectorsDone;
            double frac = Math.Clamp((double)overall / totalSectors, 0, 1);
            int pct = (int)(100.0 * frac);
            _progress.Value = pct;
            dlg.SetPercent(pct);
            double secs = (Environment.TickCount64 - startTick) / 1000.0;
            dlg.SetStats(
                string.IsNullOrEmpty(p.Detail) ? "Reading" : p.Detail,
                Rate(frac * totalBytes, secs),
                $"{overall:N0} / {totalSectors:N0}",
                Eta(frac, secs));
        });

        try
        {
            var plan = _plan;
            var file = save.FileName;
            var drv = letter.Value;
            var ct = cts.Token;

            var options = new ReadOptions
            {
                ContinueOnError = _continueOnError.Checked,
                CorrectJitter = _jitter.Checked,
            };

            // Write to a .partial file and only name it .cdi once the trailer is
            // written. A CDI's version magic lives at EOF, so a rip that dies
            // part-way leaves a file that LOOKS like an image but has no trailer
            // — and every tool, ours included, then reports a confusing
            // "not a CDI image" for what is really a truncated read.
            var partial = file + ".partial";

            ReadReport report;
            try
            {
                report = await Task.Run(() =>
                {
                    using var os = File.Create(partial);
                    return DiscReader.ReadToCdi(drv, plan, CdiVersion.V35, os, progress, options, ct);
                }, ct);
            }
            catch
            {
                TryDelete(partial);
                throw;
            }

            if (File.Exists(file)) File.Delete(file);
            File.Move(partial, file);

            _progress.Value = 100;
// Sub-channel is a second pass, after the image is safely written.
            // Some discs carry meaning there that the main data does not —
            // LibCrypt corrupts specific Q frames deliberately, and software
            // checks those exact positions — so an image without the sidecar can
            // be byte-perfect and still refuse to run.
            //
            // A drive that won't return raw P–W is a refusal rather than a
            // failure: the image is already complete and unaffected.
            if (_captureSub.Checked)
            {
                _log.Add("Reading sub-channel…");
                dlg.SetTitleBase("Reading sub-channel");
                try
                {
                    long subStart = Environment.TickCount64;
                    var subProgress = new Progress<double>(f =>
                    {
                        int sp = Math.Clamp((int)(f * 100), 0, 100);
                        _progress.Value = sp;
                        dlg.SetPercent(sp);
                        double ss = (Environment.TickCount64 - subStart) / 1000.0;
                        dlg.SetStats("Sub-channel", "—",
                            $"{(uint)(f * totalSectors):N0} / {totalSectors:N0}", Eta(f, ss));
                    });

                    var capture = await Task.Run(() =>
                        SubchannelCapture.Capture(drv, file, 0, totalSectors, subProgress, ct));

                    if (capture is null)
                    {
                        _log.Add("This drive will not return raw sub-channel, so no sidecar was " +
                                 "written. The image itself is unaffected.", EventLogView.Level.Warn);
                    }
                    else
                    {
                        _log.Add(SubchannelCapture.Describe(capture),
                            capture.Analysis.LooksLikeLibCrypt
                                ? EventLogView.Level.Warn
                                : EventLogView.Level.Good);

                        if (!capture.Complete)
                            _log.Add($"{capture.SectorsRefused:N0} sector(s) of sub-channel could " +
                                     "not be read and are zeroed in the sidecar.",
                                EventLogView.Level.Warn);

                        AppLog.Write($"  subchannel: {capture.SectorsWritten:N0} frames, " +
                                     $"{capture.Analysis.QInvalid} invalid Q, " +
                                     $"libcrypt={capture.Analysis.LooksLikeLibCrypt}");

                        // When the disc carries LibCrypt, also write the compact,
                        // emulator-ready .sbi beside the image — the same
                        // preservation data as the .sub, in the portable form.
                        if (capture.Analysis.LooksLikeLibCrypt)
                            TryWriteSbi(capture.Path, file);
                    }
                }
                catch (Exception ex)
                {
                    _log.Add("Sub-channel capture failed: " + ex.Message + " — the image is " +
                             "complete regardless.", EventLogView.Level.Warn);
                    AppLog.WriteException("subchannel capture", ex);
                }
                _progress.Value = 100;
            }

            foreach (var note in report.Notes) AppLog.Write("    " + note);

            if (report.Complete)
            {
                _log.Add($"Read complete: {Path.GetFileName(file)}", EventLogView.Level.Good);
                _log.Add("Every sector read cleanly. Tip: run Inspect ▸ Verify to CRC-check it.");
            }
            else if (report.CompleteExceptBoundaries)
            {
                // Pregap and run-out sectors are padding: the drive won't position
                // against them on some hardware. Distinguish that from damage,
                // because the payload here is intact and the image is usable.
                _log.Add($"Read complete: {Path.GetFileName(file)}", EventLogView.Level.Good);
                _log.Add($"{report.BoundarySectors.Count:N0} track-boundary sector(s) could not be " +
                         "positioned and were zero-filled. These are pregap or run-out padding, " +
                         "not disc damage — the data is intact.", EventLogView.Level.Warn);
                foreach (var lba in report.BoundarySectors.Take(20))
                    _log.Add($"  boundary sector at LBA {lba:N0}", EventLogView.Level.Warn);
            }
            else
            {
                // Be blunt: a partial image must never look like a clean one.
                _log.Add($"Read finished with {report.BadSectors.Count:N0} UNREADABLE sector(s).",
                    EventLogView.Level.Error);
                _log.Add("This image is INCOMPLETE — unreadable sectors were zero-filled.",
                    EventLogView.Level.Error);
                foreach (var lba in report.BadSectors.Take(20))
                    _log.Add($"  bad sector at LBA {lba:N0}", EventLogView.Level.Warn);
                if (report.BadSectors.Count > 20)
                    _log.Add($"  … and {report.BadSectors.Count - 20:N0} more", EventLogView.Level.Warn);
            }

            ok = true;
        }
        catch (OperationCanceledException)
        {
            _log.Add("Read cancelled — no image was written.", EventLogView.Level.Warn);
        }
        catch (Exception ex)
        {
            _log.Add("Read failed: " + ex.Message, EventLogView.Level.Error);
            _log.Add("No image was written — the incomplete data has been discarded.",
                EventLogView.Level.Warn);
            AppLog.WriteException("disc read", ex);
        }
        finally
        {
            dlg.Finish(ok);
            _rip.Enabled = true;
        }
    }

    private static string Rate(double bytesDone, double secs)
        => secs > 0.7 ? $"{bytesDone / (1024.0 * 1024.0) / secs:0.0} MB/s" : "—";

    private static string Eta(double frac, double secs)
    {
        if (frac <= 0.001 || secs < 0.7) return "—";
        double left = Math.Max(0, secs / frac - secs);
        var ts = TimeSpan.FromSeconds(left);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }

    /// <summary>Best-effort: turn a freshly-captured LibCrypt .sub into a portable
    /// .sbi beside the image. Preservation, not circumvention — it copies the
    /// disc's own subchannel so an emulator reproduces it faithfully.</summary>
    private void TryWriteSbi(string subPath, string imagePath)
    {
        try
        {
            var sub = File.ReadAllBytes(subPath);
            if (sub.Length % DiscForge.Core.Raw.RawSubchannel.FrameSize != 0) return;
            var doc = DiscForge.Core.PlayStation.Sbi.FromSubchannel(sub);
            if (doc.IsEmpty) return;
            string sbiPath = Path.ChangeExtension(imagePath, ".sbi");
            File.WriteAllBytes(sbiPath, DiscForge.Core.PlayStation.Sbi.Write(doc));
            _log.Add($"Wrote {Path.GetFileName(sbiPath)} — {doc.Entries.Count} LibCrypt entry(ies) " +
                     "for emulators that read SBI.", EventLogView.Level.Good);
        }
        catch (Exception ex)
        {
            AppLog.Write($"sbi from capture: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { AppLog.Write($"could not delete '{path}': {ex.Message}"); }
    }
}