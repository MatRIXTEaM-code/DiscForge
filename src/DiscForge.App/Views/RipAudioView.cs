// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DiscForge.Core.Audio;
using DiscForge.Core.Devices;
using DiscForge.Devices;
using DiscForge.Devices.Media;
using DiscForge.Devices.Reading;

namespace DiscForge.App.Views;

/// <summary>
/// Rip an audio CD to WAV files, with jitter correction, CD-TEXT naming and
/// AccurateRip checksums.
///
/// The two things that separate a serious ripper from a naive one are both
/// here. Jitter correction, because CD-DA sectors carry no header and a drive
/// may hand back audio a few samples off from where it was asked — concatenating
/// those blindly puts a click at every join. And AccurateRip, because a rip that
/// looks fine may not be: comparing checksums against everyone else's rip of the
/// same pressing catches silent mis-reads that report no error at all.
/// </summary>
internal sealed class RipAudioView : UserControl
{
    private readonly ComboBox _drives = new()
    {
        Width = 230, DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Ui,
        Location = new Point(70, 13),
    };
    private readonly CheckBox _jitter = new()
    {
        Text = "Correct jitter (slower, more accurate)", AutoSize = true,
        Location = new Point(12, 48), Font = Theme.Ui, Checked = true,
    };
    private readonly CheckBox _useCdText = new()
    {
        Text = "Name files from CD-TEXT where the disc has it", AutoSize = true,
        Location = new Point(12, 70), Font = Theme.Ui, Checked = true,
    };
    private readonly CheckBox _continueOnError = new()
    {
        Text = "Continue past unreadable sectors (fills them with silence)", AutoSize = true,
        Location = new Point(360, 48), Font = Theme.Ui,
    };
    private readonly Button _rip = new()
    {
        Text = "Rip to WAV…", Location = new Point(458, 12), Width = 86, Height = 26,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Button _cancel = new()
    {
        Text = "Cancel", Location = new Point(550, 12), Width = 82, Height = 26,
        FlatStyle = FlatStyle.System, Visible = false,
    };
    private readonly Button _saveLog = new()
    {
        Text = "Save log…", Location = new Point(638, 12), Width = 86, Height = 26,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Label _elapsed = new()
    {
        AutoSize = false, Location = new Point(620, 70), Size = new Size(104, 16),
        Font = Theme.Ui, ForeColor = Color.Gray, TextAlign = ContentAlignment.MiddleRight,
        Anchor = AnchorStyles.Top | AnchorStyles.Right,
    };
    private readonly ListView _tracks = new()
    {
        Location = new Point(12, 96), Size = new Size(712, 150),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        Font = Theme.Ui, BackColor = Color.White,
    };
    private readonly ProgressBar _progress = new()
    {
        Location = new Point(12, 254), Size = new Size(712, 18), Minimum = 0, Maximum = 100,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly TextBox _out = new()
    {
        Location = new Point(12, 280), Size = new Size(712, 168),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        BackColor = Color.White, WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 8.5f),
    };

    private readonly OperationRunner _runner;
    private IReadOnlyList<DriveCapabilities> _detected = Array.Empty<DriveCapabilities>();
    private AudioRipPlan? _plan;
    private CdTextInfo _cdText = CdTextInfo.None;
    private OperationLog? _log;

    public RipAudioView()
    {
        Size = new Size(736, 470);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Drive:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });

        var detect = new Button
        {
            Text = "Detect", Location = new Point(306, 12), Width = 62, FlatStyle = FlatStyle.System,
        };
        detect.Click += async (_, _) => await DetectAsync();

        var eject = new Button
        {
            Text = "Eject", Location = new Point(374, 12), Width = 56, FlatStyle = FlatStyle.System,
        };
        eject.Click += (_, _) => Eject();

        foreach (var (name, w) in new[]
        {
            ("Track", 50), ("Title", 250), ("Start LBA", 80), ("Duration", 80), ("Size", 90),
        })
            _tracks.Columns.Add(new ColumnHeader { Text = name, Width = w });

        _rip.Click += async (_, _) => await RipAsync();
        _saveLog.Click += (_, _) => SaveLog();
        _drives.SelectedIndexChanged += async (_, _) => await ReadTocAsync();
        _useCdText.CheckedChanged += (_, _) => RefreshTrackList();

        _runner = new OperationRunner(_rip, _cancel, _elapsed);

        Controls.Add(_drives); Controls.Add(detect); Controls.Add(eject);
        Controls.Add(_rip); Controls.Add(_cancel); Controls.Add(_saveLog); Controls.Add(_elapsed);
        Controls.Add(_jitter); Controls.Add(_useCdText); Controls.Add(_continueOnError);
        Controls.Add(_tracks); Controls.Add(_progress); Controls.Add(_out);

        // Prose flows to the window; only the rip REPORT (whose columns are
        // monospace-aligned) needs wrap off — ShowProse/ShowReport switch modes.
        ShowProse(
            "Detect a drive with an audio CD in it." + Environment.NewLine +
            Environment.NewLine +
            "Jitter correction reads overlapping chunks and aligns them by " +
            "correlation. CD-DA sectors carry no header, so a drive can return " +
            "audio a few samples either side of where it was asked — differently " +
            "each time. Joining those blindly clicks at every seam." + Environment.NewLine +
            Environment.NewLine +
            "AccurateRip checksums are computed as the rip runs. Comparing them " +
            "against the database tells you whether your rip matches everyone " +
            "else's of the same pressing — which catches errors nothing reported.");
    }

    /// <summary>Flowing text (help, errors): wrap to the window, whatever its size.</summary>
    private void ShowProse(string text) { _out.WordWrap = true; _out.Text = text; }

    /// <summary>Column-aligned monospace report: authored line breaks are the layout.</summary>
    private void ShowReport(string text) { _out.WordWrap = false; _out.Text = text; }

    private async Task DetectAsync()
    {
        _drives.Items.Clear();
        _tracks.Items.Clear();
        _rip.Enabled = false;
        _plan = null;
        _cdText = CdTextInfo.None;
        ShowProse("Detecting…");
        try
        {
            _detected = await Task.Run(() => DriveDetector.DetectAll());
            foreach (var d in _detected) _drives.Items.Add(d.Summary());
            if (_drives.Items.Count > 0) _drives.SelectedIndex = 0;
            else ShowProse("No optical drives detected (raw access usually needs administrator).");
        }
        catch (Exception ex)
        {
            ShowProse("Detection failed: " + ex.Message);
            AppLog.WriteException("rip detect", ex);
        }
    }

    private char? SelectedLetter()
    {
        if (_drives.SelectedIndex < 0 || _drives.SelectedIndex >= _detected.Count) return null;
        var path = _detected[_drives.SelectedIndex].DevicePath;
        int i = path.LastIndexOf(':');
        return i > 0 ? path[i - 1] : null;
    }

    private void Eject()
    {
        var letter = SelectedLetter();
        if (letter is null) return;
        try
        {
            DriveTray.Eject(letter.Value);
            _tracks.Items.Clear();
            _plan = null;
            _rip.Enabled = false;
            ShowProse("Tray ejected. Insert a disc and press Detect again.");
        }
        catch (Exception ex)
        {
            ShowProse("Could not eject: " + ex.Message);
        }
    }

    private void SaveLog()
    {
        if (_log is null) return;
        var path = _log.SaveWithDialog();
        if (path is not null) StatusBus.Report($"Log saved to {Path.GetFileName(path)}");
    }

    private async Task ReadTocAsync()
    {
        var letter = SelectedLetter();
        if (letter is null) return;

        var drive = _detected[_drives.SelectedIndex];
        _tracks.Items.Clear();
        _rip.Enabled = false;
        _plan = null;
        _cdText = CdTextInfo.None;
        ShowProse("Reading the table of contents…");

        try
        {
            var toc = await Task.Run(() => DiscReader.ReadToc(letter.Value));
            _plan = AudioRipPlanner.Plan(toc, drive);

            // CD-TEXT is optional and most discs have none, so this is a bonus
            // rather than a step that can fail the rip.
            _cdText = await Task.Run(() => AudioRipper.ReadCdText(letter.Value));

            RefreshTrackList();

            var sb = new StringBuilder();
            sb.AppendLine($"{_plan.Tracks.Count} audio track(s), " +
                          $"{(int)_plan.TotalDuration.TotalMinutes}:{_plan.TotalDuration.Seconds:D2}, " +
                          $"{_plan.TotalPcmBytes / (1024.0 * 1024.0):N0} MB of WAV");
            sb.AppendLine();

            if (_cdText.Any)
            {
                sb.AppendLine($"CD-TEXT: \"{_cdText.AlbumTitle ?? "(untitled)"}\"" +
                              (_cdText.AlbumPerformer is not null ? $" — {_cdText.AlbumPerformer}" : "") +
                              $", {_cdText.TrackTitles.Count} track title(s).");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("No CD-TEXT on this disc — files will be numbered. Most discs have " +
                              "none; it was optional and rarely used.");
                sb.AppendLine();
            }

            // Accurate stream tells you whether jitter correction is doing real
            // work or merely costing time — worth surfacing rather than leaving
            // the checkbox as a mystery.
            var caps = await Task.Run(() =>
            {
                try { return MediaInfoReader.Read(letter.Value).Capabilities; }
                catch { return null; }
            });
            if (caps is not null)
            {
                if (caps.CddaAccurateStream)
                {
                    sb.AppendLine("This drive reports \"accurate stream\": it returns audio from where it " +
                                  "was asked, so jitter correction will find little to fix. Leaving it on " +
                                  "costs only the overlap re-read.");
                }
                else
                {
                    sb.AppendLine("This drive does NOT report \"accurate stream\": it may return audio " +
                                  "offset from where it was asked, and differently each time. Leave jitter " +
                                  "correction on, or the joins between reads will click.");
                }
                sb.AppendLine();
            }

            foreach (var w in _plan.Warnings) sb.AppendLine("Note: " + w);
            if (_plan.Warnings.Count > 0) sb.AppendLine();

            sb.AppendLine("Press \"Rip to WAV…\" and choose where the files should go.");
            ShowProse(sb.ToString());
            _rip.Enabled = true;
        }
        catch (Exception ex)
        {
            ShowProse(ex.Message);
            AppLog.WriteException("rip toc", ex);
        }
    }

    /// <summary>Names each track for display and for the file it will become.</summary>
    private string TitleFor(int trackNumber) =>
        _useCdText.Checked && _cdText.TrackTitles.TryGetValue(trackNumber, out var t) ? t : "";

    private void RefreshTrackList()
    {
        if (_plan is null) return;
        _tracks.Items.Clear();

        foreach (var t in _plan.Tracks)
        {
            var item = new ListViewItem(t.Number.ToString());
            string title = TitleFor(t.Number);
            item.SubItems.Add(title.Length > 0 ? title : "(no CD-TEXT)");
            if (title.Length == 0) item.SubItems[1].ForeColor = Color.Gray;
            item.SubItems.Add(t.StartLba.ToString("N0"));
            item.SubItems.Add($"{(int)t.Duration.TotalMinutes}:{t.Duration.Seconds:D2}");
            item.SubItems.Add($"{t.PcmBytes / (1024.0 * 1024.0):N1} MB");
            _tracks.Items.Add(item);
        }
    }

    private async Task RipAsync()
    {
        var letter = SelectedLetter();
        if (letter is null || _plan is null) return;

        var drive = _detected[_drives.SelectedIndex];

        using var folder = new FolderBrowserDialog
        {
            Description = "Save the WAV files to…",
            SelectedPath = AppSettings.LastRipDirectory ?? "",
        };
        if (folder.ShowDialog() != DialogResult.OK) return;

        AppSettings.LastRipDirectory = folder.SelectedPath;

        _progress.Value = 0;
        _saveLog.Enabled = false;
        ShowProse("Ripping…");

        // Rebuild the plan with CD-TEXT filenames where they exist. The planner
        // sanitises them: a title can contain anything at all, and one that
        // becomes an illegal filename would fail the whole rip on whichever
        // track happened to carry it.
        var plan = _plan with
        {
            Tracks = _plan.Tracks.Select(t => t with
            {
                Filename = AudioRipPlanner.SafeFilename(t.Number, TitleFor(t.Number)),
            }).ToList(),
        };

        var dir = folder.SelectedPath;
        var opts = new AudioRipOptions
        {
            CorrectJitter = _jitter.Checked,
            ContinueOnError = _continueOnError.Checked,
        };

        var result = await _runner.RunAsync(cancel =>
        {
            var progress = new Progress<AudioRipProgress>(p =>
                BeginInvoke(() => _progress.Value = Math.Clamp((int)(p.Fraction * 100), 0, 100)));
            return AudioRipper.Rip(letter.Value, plan, dir, opts, progress, cancel);
        },
        ex =>
        {
            ShowProse("Rip failed: " + ex.Message);
            AppLog.WriteException("audio rip", ex);
        });

        if (result is null)
        {
            // Cancelled. Whatever tracks finished are complete files — the rest
            // were written to .partial and cleaned up, so nothing half-written
            // is left claiming to be a WAV.
            ShowProse(
                "Rip cancelled." + Environment.NewLine +
                Environment.NewLine +
                "Tracks that finished before you stopped are complete and playable. " +
                "The one in progress was discarded rather than left half-written — a " +
                "WAV declares its own length, so a truncated one looks complete to a " +
                "player and stops early.");
            _progress.Value = 0;
            StatusBus.Report("Rip cancelled");
            return;
        }

        _progress.Value = 100;
        string text = Render(result, dir, _cdText);
        ShowReport(text);

        // The log carries the AccurateRip checksums alongside the drive that
        // produced them, which is what makes them comparable: a rip is verified
        // against other people's of the same pressing, and knowing the drive
        // explains a mismatch that would otherwise look like disc damage.
        var log = new OperationLog("Audio CD rip");
        try
        {
            var info = await Task.Run(() => MediaInfoReader.Read(letter.Value));
            log.Drive(drive, info.Capabilities);
            log.Media(drive, info.Identity);
        }
        catch
        {
            log.Drive(drive);
            log.Media(drive);
        }
        log.Settings(
            ("Tracks", result.Tracks.Count),
            ("Output", dir),
            ("Jitter correction", _jitter.Checked ? "on" : "off"),
            ("CD-TEXT naming", _useCdText.Checked ? "on" : "off"),
            ("Continue on error", _continueOnError.Checked ? "on" : "off"),
            ("AccurateRip disc ID", $"{result.DiscId1:X8}-{result.DiscId2:X8}-{result.CddbId:X8}"));
        log.Result(text);
        _log = log;
        _saveLog.Enabled = true;

        AppLog.Write($"  audio rip: {result.Tracks.Count} track(s), " +
                     $"{result.TotalBadSectors} bad sector(s), " +
                     $"AR id {result.DiscId1:X8}-{result.DiscId2:X8}");
        StatusBus.Report(result.AllClean
            ? $"Ripped {result.Tracks.Count} track(s) cleanly"
            : $"Ripped with {result.TotalBadSectors} bad sector(s)");
    }

    private static string Render(AudioRipResult r, string dir, CdTextInfo cdText)
    {
        var sb = new StringBuilder();

        if (cdText.AlbumTitle is not null)
            sb.AppendLine($"\"{cdText.AlbumTitle}\"" +
                          (cdText.AlbumPerformer is not null ? $" — {cdText.AlbumPerformer}" : ""));

        sb.AppendLine($"Ripped {r.Tracks.Count} track(s) to {dir} in " +
                      $"{(int)r.Elapsed.TotalMinutes}:{r.Elapsed.Seconds:D2}");
        sb.AppendLine();
        sb.AppendLine("  Track  File                                Size      Bad  AccurateRip v1  v2");
        sb.AppendLine("  -----  ----------------------------------  --------  ---  --------------  --------");

        foreach (var t in r.Tracks)
        {
            string name = Path.GetFileName(t.Path);
            if (name.Length > 34) name = name[..31] + "...";
            sb.AppendLine(
                $"  {t.Number,5}  {name,-34}  " +
                $"{t.BytesWritten / (1024.0 * 1024.0),5:N1} MB  " +
                $"{t.BadSectors,3}  {t.AccurateRipV1,14:X8}  {t.AccurateRipV2:X8}");
        }

        sb.AppendLine();
        sb.AppendLine($"  Disc IDs: AR1={r.DiscId1:X8}  AR2={r.DiscId2:X8}  CDDB={r.CddbId:X8}");
        sb.AppendLine();

        if (r.AllClean)
        {
            sb.AppendLine("Every sector read without error.");
        }
        else
        {
            sb.AppendLine($"{r.TotalBadSectors:N0} sector(s) could not be read and were filled with");
            sb.AppendLine("silence. Those tracks have audible gaps — clean the disc and rip again,");
            sb.AppendLine("or try a different drive: drives differ considerably in what they can");
            sb.AppendLine("read from a damaged disc.");
        }
        sb.AppendLine();

        sb.AppendLine("To verify this rip against other people's of the same pressing, fetch the");
        sb.AppendLine("AccurateRip record and compare the checksums above:");
        sb.AppendLine();
        sb.AppendLine("  " + r.LookupUrl);
        sb.AppendLine();
        sb.AppendLine("A match means your rip is bit-identical to theirs — which catches errors");
        sb.AppendLine("that no drive reported, and is a stronger claim than \"no errors occurred\".");

        if (r.Problems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Notes:");
            foreach (var p in r.Problems.Take(30)) sb.AppendLine("  " + p);
            if (r.Problems.Count > 30)
                sb.AppendLine($"  … and {r.Problems.Count - 30:N0} more (see the log).");
        }

        return sb.ToString();
    }
}