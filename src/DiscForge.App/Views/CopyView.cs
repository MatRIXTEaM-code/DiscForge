// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Burning;
using DiscForge.Core.Cdi;
using DiscForge.Core.Copying;
using DiscForge.Core.Devices;
using DiscForge.Core.Mmc;
using DiscForge.Devices;
using DiscForge.Devices.Burning;
using DiscForge.Devices.Reading;

namespace DiscForge.App.Views;

/// <summary>
/// Copy a disc: read the source, then burn what was read to one or more
/// destinations (drives, and/or an image file).
///
/// The whole copy is planned by the pure CopyPlanner BEFORE anything is read, so
/// an impossible copy — say an audio disc on a burner with no RAW DAO — is
/// refused in a second rather than after four minutes of reading.
///
/// Deliberately staged through an intermediate image rather than "on the fly":
/// a reader pausing to retry a marginal sector would starve a burner, staging
/// lets the image be verified first, and it's the only way a single drive can
/// copy at all (read, swap, burn).
/// </summary>
internal sealed class CopyView : UserControl
{
    private readonly ComboBox _source = new()
    {
        Width = 290, DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Ui,
        Location = new Point(70, 13),
    };
    private readonly ListView _destinations = new()
    {
        Location = new Point(12, 68), Size = new Size(712, 84),
        View = View.Details, CheckBoxes = true, FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable, Font = Theme.Ui, BackColor = Color.White,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly CheckBox _verify = new()
    {
        Text = "Verify each copy", AutoSize = true, Location = new Point(12, 164), Font = Theme.Ui,
    };
    private readonly CheckBox _keepImage = new()
    {
        Text = "Keep the intermediate image", AutoSize = true, Location = new Point(12, 186), Font = Theme.Ui,
    };
    private readonly NumericUpDown _copies = new()
    {
        Minimum = 1, Maximum = 99, Value = 1, Width = 52,
        Location = new Point(250, 162), Font = Theme.Ui,
    };
    private readonly Button _plan = new()
    {
        Text = "Plan copy", Location = new Point(340, 160), Width = 96, FlatStyle = FlatStyle.System,
    };
    private readonly Button _start = new()
    {
        Text = "Copy", Location = new Point(444, 160), Width = 96, Enabled = false,
        FlatStyle = FlatStyle.System,
    };
    private readonly ProgressBar _progress = new()
    {
        Location = new Point(12, 216), Size = new Size(712, 20), Minimum = 0, Maximum = 100,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly EventLogView _log = new()
    {
        Location = new Point(12, 248), Size = new Size(712, 212),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
    };

    private IReadOnlyList<DriveCapabilities> _detected = Array.Empty<DriveCapabilities>();
    private DiscToc? _toc;
    private CopyPlan? _copyPlan;

    public CopyView()
    {
        // Establish a realistic size before adding anchored children (see InspectView).
        Size = new Size(736, 470);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Source:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });

        var detect = new Button { Text = "Detect", Location = new Point(368, 12), Width = 74, FlatStyle = FlatStyle.System };
        detect.Click += async (_, _) => await DetectAsync();

        var readToc = new Button { Text = "Read source TOC", Location = new Point(448, 12), Width = 110, FlatStyle = FlatStyle.System };
        readToc.Click += async (_, _) => await ReadTocAsync();

        Controls.Add(new Label
        {
            Text = "Copy to:", AutoSize = true, Location = new Point(12, 50), Font = Theme.UiBold,
        });
        var addFile = new Button
        {
            Text = "Image file…", Location = new Point(642, 46), Width = 82, FlatStyle = FlatStyle.System,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        addFile.Click += (_, _) => AddImageDestination();

        _destinations.Columns.Add("Destination", 460);
        _destinations.Columns.Add("Capabilities", 235);
        _destinations.ItemChecked += (_, _) => { _copyPlan = null; _start.Enabled = false; };

        Controls.Add(new Label { Text = "Copies:", AutoSize = true, Location = new Point(196, 164), Font = Theme.Ui });

        _plan.Click += (_, _) => BuildPlan();
        _start.Click += async (_, _) => await CopyAsync();

        Controls.Add(_source); Controls.Add(detect); Controls.Add(readToc);
        Controls.Add(addFile);
        Controls.Add(_destinations);
        Controls.Add(_verify); Controls.Add(_keepImage); Controls.Add(_copies);
        Controls.Add(_plan); Controls.Add(_start);
        Controls.Add(_progress);
        Controls.Add(_log);

        _log.Add("Detect drives, insert the source disc, then read its TOC.");
    }

    // --- drives ---------------------------------------------------------------

    private async Task DetectAsync()
    {
        _source.Items.Clear();
        _destinations.Items.Clear();
        _copyPlan = null;
        _start.Enabled = false;
        _log.Add("Detecting drives…");
        try
        {
            _detected = await Task.Run(() => DriveDetector.DetectAll());
            foreach (var d in _detected)
            {
                _source.Items.Add(d.Summary());

                var item = new ListViewItem($"{d.Vendor} {d.Model} ({d.DevicePath})") { Tag = d };
                item.SubItems.Add(d.Summary());
                _destinations.Items.Add(item);

                AppLog.Write($"  drive {d.DevicePath}: rawDAO96={d.RawDao96} media={d.MediaProfile} " +
                             $"CDw={d.CdWrite} DVDw={d.DvdWrite}");
            }
            if (_source.Items.Count > 0) _source.SelectedIndex = 0;
            _log.Add(_detected.Count == 0
                    ? "No optical drives detected (raw access usually needs administrator)."
                    : $"{_detected.Count} drive(s) detected.",
                _detected.Count == 0 ? EventLogView.Level.Warn : EventLogView.Level.Good);
        }
        catch (Exception ex)
        {
            _log.Add("Detection failed: " + ex.Message, EventLogView.Level.Error);
            AppLog.WriteException("copy detect", ex);
        }
    }

    private DriveCapabilities? SourceDrive() =>
        _source.SelectedIndex >= 0 && _source.SelectedIndex < _detected.Count
            ? _detected[_source.SelectedIndex]
            : null;

    private static char? LetterOf(DriveCapabilities drive)
    {
        var path = drive.DevicePath;           // e.g. \\.\D:
        int i = path.LastIndexOf(':');
        return i > 0 ? path[i - 1] : null;
    }

    private void AddImageDestination()
    {
        using var dlg = new SaveFileDialog { Filter = "CDI image (*.cdi)|*.cdi", FileName = "copy.cdi" };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        foreach (ListViewItem i in _destinations.Items.Cast<ListViewItem>().ToList())
            if (i.Tag is string) _destinations.Items.Remove(i);

        var item = new ListViewItem(dlg.FileName) { Tag = dlg.FileName };
        item.SubItems.Add("Image file");
        _destinations.Items.Add(item);
        item.Checked = true;
        _log.Add($"Destination: image file {Path.GetFileName(dlg.FileName)}");
    }

    private List<object> CheckedDestinations() =>
        _destinations.CheckedItems.Cast<ListViewItem>().Select(i => i.Tag!).Where(t => t is not null).ToList();

    // --- planning -------------------------------------------------------------

    private async Task ReadTocAsync()
    {
        var drive = SourceDrive();
        if (drive is null) { _log.Add("Detect and choose a source drive.", EventLogView.Level.Warn); return; }
        var letter = LetterOf(drive);
        if (letter is null) return;

        _copyPlan = null;
        _start.Enabled = false;
        _log.Add($"Reading TOC from {letter}:…");
        try
        {
            _toc = await Task.Run(() => DiscReader.ReadToc(letter.Value));
            _log.Add($"Media: {drive.MediaProfile}");
            _log.Add($"Source: tracks {_toc.FirstTrack}–{_toc.LastTrack}, lead-out at LBA {_toc.LeadOutLba:N0}.",
                EventLogView.Level.Good);
            if (_toc.IsMixedMode) _log.Add("Mixed-mode disc (audio + data).", EventLogView.Level.Warn);
            _log.Add("Now tick the destinations and press Plan copy.");
        }
        catch (Exception ex)
        {
            _toc = null;
            _log.Add("Read TOC failed: " + ex.Message, EventLogView.Level.Error);
            AppLog.WriteException("copy read toc", ex);
        }
    }

    private void BuildPlan()
    {
        _copyPlan = null;
        _start.Enabled = false;

        var drive = SourceDrive();
        if (drive is null || _toc is null)
        {
            _log.Add("Read the source disc's TOC first.", EventLogView.Level.Warn);
            return;
        }

        var dests = CheckedDestinations();
        if (dests.Count == 0) { _log.Add("Tick at least one destination.", EventLogView.Level.Warn); return; }

        var job = new CopyJob
        {
            Source = drive,
            Destinations = dests.Select(ToDestination).ToList(),
            Verify = _verify.Checked,
            Copies = (int)_copies.Value,
        };

        try
        {
            // Ask the disc what its sectors actually are before planning. The TOC
            // separates audio from data and nothing more, so without this a Mode 2
            // Form 2 track — SVCD, VCD, CD-i — looks like plain Mode 1 and gets
            // planned as a cooked read the drive refuses outright.
            var letter = LetterOf(drive);
            var modes = letter is null
                ? null
                : TrackModeProber.Probe(letter.Value, _toc);
            if (modes is not null)
                foreach (var kv in modes)
                    AppLog.Write($"    probe: track {kv.Key} sectors are {kv.Value}");

            // Planned BEFORE reading: an impossible copy costs a second, not four
            // minutes of reading followed by disappointment.
            var plan = CopyPlanner.Plan(_toc, job, modes);
            _copyPlan = plan;

            AppLog.Write($"  plan: raw={plan.Read.RawMode} rawRequired={plan.Read.RawRequired} " +
                         $"tracks={plan.Read.Tracks.Count} totalBytes={plan.Read.TotalBytes:N0}");
            foreach (var t in plan.Read.Tracks)
                AppLog.Write($"    track {t.Number}: {(t.IsAudio ? "audio" : "data")} " +
                             $"lba={t.StartLba} sectors={t.LengthSectors} " +
                             $"sectorSize={(int)t.SectorSize} mode={t.Mode} detected={t.Detected}");

            _log.Add($"Image will be {plan.Shape.TrackCount} track(s), " +
                     $"{plan.ImageBytes / (1024.0 * 1024.0):N1} MB" +
                     (plan.Read.RawMode ? " (raw)" : " (cooked)") +
                     (plan.Shape.HasAudio ? " (audio)" : ""));
            foreach (var w in plan.Warnings) _log.Add(w, EventLogView.Level.Warn);
            foreach (var d in plan.Burn.Refused)
                _log.Add($"{d.Label}: CANNOT COPY — {d.Refusal}", EventLogView.Level.Error);
            foreach (var d in plan.Burn.Runnable)
                _log.Add($"Will copy to {d.Label} ({d.Steps.Count} step(s))", EventLogView.Level.Good);

            _start.Enabled = true;
        }
        catch (Exception ex)
        {
            // The useful case: "requires RAW DAO-96, which this drive doesn't support".
            _log.Add("Copy not possible: " + ex.Message, EventLogView.Level.Error);
        }
    }

    private static BurnDestination ToDestination(object tag) => tag switch
    {
        DriveCapabilities d => new BurnDestination.Drive(d),
        string path => new BurnDestination.ImageFile(path),
        _ => throw new InvalidOperationException("Unknown destination."),
    };

    // --- doing it -------------------------------------------------------------

    private async Task CopyAsync()
    {
        if (_copyPlan is not { } plan) return;
        var drive = SourceDrive();
        if (drive is null) return;
        var letter = LetterOf(drive);
        if (letter is null) return;

        // Where the intermediate lives. Kept only if asked.
        string imagePath;
        bool keep = _keepImage.Checked;
        if (keep)
        {
            using var dlg = new SaveFileDialog { Filter = "CDI image (*.cdi)|*.cdi", FileName = "copy-source.cdi" };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            imagePath = dlg.FileName;
        }
        else
        {
            imagePath = Path.Combine(Path.GetTempPath(), "ojug_copy_" + Guid.NewGuid().ToString("N") + ".cdi");
        }

        _start.Enabled = false;
        _plan.Enabled = false;
        _progress.Value = 0;

        // A DVD-Shrink-style window spans the whole copy: reading the source is
        // the first half of the bar, burning the second. Cancel stops the read;
        // once burning starts it is disabled, because a half-written disc is a
        // coaster whatever you do.
        using var cts = new CancellationTokenSource();
        RipProgressDialog? prog = null;
        bool ok = false;
        long startTick = Environment.TickCount64;
        double totalBytes = Math.Max(1.0, plan.ImageBytes);

        // Overall read fraction across all tracks (the per-track fraction alone
        // would restart the bar on every track).
        var offsets = new Dictionary<int, uint>();
        uint acc = 0;
        foreach (var t in plan.Read.Tracks) { offsets[t.Number] = acc; acc += t.LengthSectors; }
        uint totalSectors = Math.Max(1u, acc);

        try
        {
            // ---- stage 1: read the source ----
            _log.Add("--- Reading source ---");
            var problem = await Task.Run(() => DiscReader.Probe(letter.Value, plan.Read));
            if (problem is not null)
            {
                _log.Add(problem, EventLogView.Level.Error);
                return;
            }

            prog = new RipProgressDialog("Copying disc");
            prog.FormClosed += (_, _) => prog!.Dispose();
            prog.CancelRequested += () => cts.Cancel();
            prog.Show(this);                 // create the window (and its handle) first
            prog.SetTitleBase("Reading source");

            var readProgress = new Progress<ReadProgress>(p =>
            {
                uint overall = (offsets.TryGetValue(p.TrackNumber, out var before) ? before : 0) + p.SectorsDone;
                double frac = Math.Clamp((double)overall / totalSectors, 0, 1);
                int half = (int)(frac * 50);   // read is the first half of the bar
                _progress.Value = half;
                prog!.SetPercent(half);
                double secs = (Environment.TickCount64 - startTick) / 1000.0;
                prog!.SetStats(
                    string.IsNullOrEmpty(p.Detail) ? "Reading source" : p.Detail,
                    Rate(frac * totalBytes, secs),
                    $"{overall:N0} / {totalSectors:N0}",
                    Eta(frac, secs));
            });

            var partial = imagePath + ".partial";
            var ct = cts.Token;
            ReadReport report;
            try
            {
                report = await Task.Run(() =>
                {
                    using var os = File.Create(partial);
                    return DiscReader.ReadToCdi(letter.Value, plan.Read, CdiVersion.V35, os, readProgress, null, ct);
                }, ct);
            }
            catch
            {
                TryDelete(partial);
                throw;
            }

            if (File.Exists(imagePath)) File.Delete(imagePath);
            File.Move(partial, imagePath);

            foreach (var note in report.Notes) AppLog.Write("    " + note);

            // Holes only at track boundaries are pregap or run-out padding that
            // some drives won't position against — not damage, and no reason to
            // abandon a copy. Real unreadable sectors still stop it.
            if (!report.Complete && !report.CompleteExceptBoundaries)
            {
                _log.Add($"Source had {report.BadSectors.Count:N0} unreadable sector(s) — " +
                         "the copy would be incomplete. Stopping.", EventLogView.Level.Error);
                _log.Add("Read the disc on its own with \"continue past unreadable sectors\" if you " +
                         "want to salvage what you can.", EventLogView.Level.Warn);
                return;
            }
            if (report.CompleteExceptBoundaries)
                _log.Add($"{report.BoundarySectors.Count:N0} track-boundary sector(s) were zero-filled " +
                         "(pregap or run-out padding, not damage). Continuing.", EventLogView.Level.Warn);

            _log.Add($"Source read: {plan.ImageBytes / (1024.0 * 1024.0):N1} MB", EventLogView.Level.Good);

            // ---- stage 2: swap, if the source drive is also a destination ----
            if (plan.RequiresDiscSwap)
            {
                if (RetroMessageBox.Show(
                        "The source has been read to an image.\r\n\r\n" +
                        "Remove the source disc, insert a blank, and click OK to burn.",
                        "DiscForge — swap the disc", MessageBoxButtons.OKCancel,
                        MessageBoxIcon.Information) != DialogResult.OK)
                {
                    _log.Add("Cancelled before burning.", EventLogView.Level.Warn);
                    return;
                }
            }
            else if (plan.Burn.Runnable.Any(d => !d.IsImageFile))
            {
                if (RetroMessageBox.Show("Insert blank media in the destination drive(s). Begin burning?",
                        "DiscForge", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                {
                    _log.Add("Cancelled before burning.", EventLogView.Level.Warn);
                    return;
                }
            }

            // ---- stage 3: burn ----
            _log.Add("--- Writing copies ---");
            prog?.SetTitleBase("Writing copies");
            prog?.SetCancellable(false, "Burning…");
            await BurnAllAsync(plan, imagePath, prog);
            ok = true;
        }
        catch (OperationCanceledException)
        {
            _log.Add("Copy cancelled while reading the source — nothing was written.",
                EventLogView.Level.Warn);
        }
        catch (Exception ex)
        {
            _log.Add("Copy failed: " + ex.Message, EventLogView.Level.Error);
            AppLog.WriteException("copy", ex);
        }
        finally
        {
            prog?.Finish(ok);
            if (!keep) TryDelete(imagePath);
            else _log.Add($"Intermediate image kept: {imagePath}");
            _plan.Enabled = true;
            _start.Enabled = _copyPlan is not null;
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

    private async Task BurnAllAsync(CopyPlan plan, string imagePath, RipProgressDialog? dlg)
    {
        var targets = plan.Burn.Runnable.ToList();
        var fractions = new double[targets.Count];

        var tasks = targets.Select((d, index) =>
        {
            // Built on the UI thread so its callbacks marshal back (Progress<T>
            // captures the SynchronizationContext where it is constructed).
            // Log on phase changes only — the bar carries the detail.
            string lastPhase = "";
            var progress = new Progress<BurnProgress>(p =>
            {
                fractions[index] = p.Fraction;
                // Burning is the second half of the copy.
                int overall = Math.Clamp(50 + (int)(fractions.Average() * 50), 0, 100);
                _progress.Value = overall;
                dlg?.SetPercent(overall);
                dlg?.SetStats(
                    string.IsNullOrEmpty(p.Detail) ? p.Phase : $"{p.Phase}: {p.Detail}",
                    "—", "—", "—");

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

            return Task.Run(() =>
            {
                try
                {
                    foreach (var step in d.Steps)
                    {
                        _log.Add($"[{Short(d.Label)}] {step.Kind}" +
                                 (d.TotalCopies > 1 ? $" (copy {step.CopyNumber}/{d.TotalCopies})" : "") + "…");

                        if (d.IsImageFile)
                        {
                            if (step.Kind == BurnStepKind.Write)
                                File.Copy(imagePath, d.Label, overwrite: true);
                            else if (step.Kind == BurnStepKind.Verify)
                                VerifyFile(imagePath, d.Label);
                        }
                        else
                        {
                            var caps = ((BurnDestination.Drive)d.Destination).Capabilities;
                            IBurnEngine engine = step.Method switch
                            {
                                BurnMethod.Imapi2Data => new Imapi2BurnEngine(),
                                BurnMethod.Imapi2TrackAtOnce => new Imapi2TrackAtOnceBurnEngine(),
                                _ => new RawDaoBurnEngine(),
                            };

                            using var fs = File.OpenRead(imagePath);
                            var img = CdiParser.Parse(fs);
                            engine.Burn(fs, img, new BurnPlan
                            {
                                Method = step.Method,
                                DevicePath = caps.DevicePath,
                                Warnings = Array.Empty<string>(),
                            }, progress);
                        }

                        _log.Add($"[{Short(d.Label)}] {step.Kind} done.", EventLogView.Level.Good);
                    }
                    fractions[index] = 1.0;
                    return true;
                }
                catch (Exception ex)
                {
                    _log.Add($"[{Short(d.Label)}] failed: {ex.Message}", EventLogView.Level.Error);
                    AppLog.WriteException($"copy to {d.Label}", ex);
                    return false;
                }
            });
        }).ToList();

        var results = await Task.WhenAll(tasks);
        int good = results.Count(r => r);
        int bad = results.Length - good;

        _progress.Value = 100;
        if (bad == 0)
            _log.Add($"Copy complete: {good} destination(s).", EventLogView.Level.Good);
        else
            _log.Add($"Copy finished: {good} succeeded, {bad} FAILED.",
                bad == results.Length ? EventLogView.Level.Error : EventLogView.Level.Warn);
    }

    private void VerifyFile(string source, string copy)
    {
        using var a = File.OpenRead(source);
        using var b = File.OpenRead(copy);
        var report = CdiComparer.Compare(a, CdiParser.Parse(a), b, CdiParser.Parse(b));
        if (report.Equal)
        {
            _log.Add("Verify: images are equivalent (structure + CRC-32).", EventLogView.Level.Good);
            return;
        }
        throw new InvalidDataException("Verify failed: the copy differs from the source.");
    }

    private static string Short(string label)
    {
        int open = label.LastIndexOf('(');
        if (open > 0 && label.EndsWith(')')) return label[(open + 1)..^1];
        return Path.GetFileName(label);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { AppLog.Write($"could not delete '{path}': {ex.Message}"); }
    }
}