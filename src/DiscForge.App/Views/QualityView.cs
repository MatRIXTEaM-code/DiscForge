// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DiscForge.Core.Devices;
using DiscForge.Core.Mmc;
using DiscForge.Devices;
using DiscForge.Devices.Media;
using DiscForge.Devices.Reading;
using DiscForge.Devices.Spti;

namespace DiscForge.App.Views;

/// <summary>
/// Disc quality scan: sample the surface, measure how much error correction the
/// drive is having to do, and say plainly whether the disc should be copied now.
///
/// The value is in the timing. A disc gives no warning it's dying — it reads
/// perfectly until the day it doesn't. But the drive knows: long before failure,
/// correction starts working harder, and C2 pointers make that visible. This
/// turns "it still reads" into "it still reads, but not for much longer".
/// </summary>
internal sealed class QualityView : UserControl
{
    private readonly ComboBox _drives = new()
    {
        Width = 240, DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Ui,
        Location = new Point(70, 13),
    };
    private readonly Button _scan = new()
    {
        Text = "Scan disc", Location = new Point(446, 12), Width = 86, Height = 26,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Button _cancel = new()
    {
        Text = "Cancel", Location = new Point(538, 12), Width = 82, Height = 26,
        FlatStyle = FlatStyle.System, Visible = false,
    };
    private readonly Button _saveLog = new()
    {
        Text = "Save log…", Location = new Point(626, 12), Width = 86, Height = 26,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Label _elapsed = new()
    {
        AutoSize = false, Location = new Point(658, 44), Size = new Size(66, 16),
        Font = Theme.Ui, ForeColor = Color.Gray, TextAlign = ContentAlignment.MiddleRight,
        Anchor = AnchorStyles.Top | AnchorStyles.Right,
    };
    private readonly Label _media = new()
    {
        AutoSize = false, Location = new Point(12, 44), Size = new Size(640, 16),
        Font = Theme.Ui, ForeColor = Color.Gray,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly ProgressBar _progress = new()
    {
        Location = new Point(12, 66), Size = new Size(712, 18), Minimum = 0, Maximum = 100,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly Label _verdict = new()
    {
        AutoSize = false, Location = new Point(12, 92), Size = new Size(712, 24),
        Font = new Font(Theme.Ui.FontFamily, 10f, FontStyle.Bold),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly TextBox _out = new()
    {
        Location = new Point(12, 122), Size = new Size(712, 326),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        BackColor = Color.White, WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 8.5f),
    };

    private readonly OperationRunner _runner;
    private IReadOnlyList<DriveCapabilities> _detected = Array.Empty<DriveCapabilities>();
    private uint _totalSectors;
    private OperationLog? _log;

    public QualityView()
    {
        Size = new Size(736, 470);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Drive:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });

        var detect = new Button
        {
            Text = "Detect", Location = new Point(316, 12), Width = 62, FlatStyle = FlatStyle.System,
        };
        detect.Click += async (_, _) => await DetectAsync();

        var eject = new Button
        {
            Text = "Eject", Location = new Point(384, 12), Width = 56, FlatStyle = FlatStyle.System,
        };
        eject.Click += (_, _) => Eject();

        _scan.Click += async (_, _) => await ScanAsync();
        _saveLog.Click += (_, _) => SaveLog();
        _drives.SelectedIndexChanged += async (_, _) => await CheckMediaAsync();

        _runner = new OperationRunner(_scan, _cancel, _elapsed);

        Controls.Add(_drives); Controls.Add(detect); Controls.Add(eject);
        Controls.Add(_scan); Controls.Add(_cancel); Controls.Add(_saveLog); Controls.Add(_elapsed);
        Controls.Add(_media); Controls.Add(_progress); Controls.Add(_verdict); Controls.Add(_out);

        _out.Text =
            "Detect a drive with a CD in it, then scan." + Environment.NewLine +
            Environment.NewLine +
            "A disc gives no warning before it fails — it reads perfectly until the" + Environment.NewLine +
            "day it doesn't. The drive knows sooner: long before sectors become" + Environment.NewLine +
            "unreadable, error correction starts having to work, and C2 pointers" + Environment.NewLine +
            "make that visible." + Environment.NewLine +
            Environment.NewLine +
            "This samples across the whole surface and reports where the drive is" + Environment.NewLine +
            "struggling, so a disc can be copied while copying still works." + Environment.NewLine +
            Environment.NewLine +
            "CD only — the command this uses does not exist for DVD or Blu-ray.";
    }

    private async Task DetectAsync()
    {
        _drives.Items.Clear();
        _scan.Enabled = false;
        _media.Text = "Detecting…";
        try
        {
            _detected = await Task.Run(() => DriveDetector.DetectAll());
            foreach (var d in _detected) _drives.Items.Add(d.Summary());
            if (_drives.Items.Count > 0) _drives.SelectedIndex = 0;
            else _media.Text = "No optical drives detected (raw access usually needs administrator).";
        }
        catch (Exception ex)
        {
            _media.Text = "Detection failed: " + ex.Message;
            AppLog.WriteException("quality detect", ex);
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
            _media.Text = "Tray ejected. Insert a disc and press Detect again.";
            _media.ForeColor = Color.Gray;
            _scan.Enabled = false;
        }
        catch (Exception ex)
        {
            _media.Text = "Could not eject: " + ex.Message;
            _media.ForeColor = Color.FromArgb(0xA0, 0x60, 0x00);
        }
    }

    private void SaveLog()
    {
        if (_log is null) return;
        var path = _log.SaveWithDialog();
        if (path is not null) StatusBus.Report($"Log saved to {Path.GetFileName(path)}");
    }

    private async Task CheckMediaAsync()
    {
        var letter = SelectedLetter();
        if (letter is null) { _scan.Enabled = false; return; }

        var drive = _detected[_drives.SelectedIndex];
        bool isCd = drive.MediaProfile is MmcProfile.CdRom or MmcProfile.CdR or MmcProfile.CdRw;

        if (!isCd)
        {
            _media.Text = $"{drive.MediaProfile} loaded — quality scanning is a CD-only feature.";
            _media.ForeColor = Color.FromArgb(0xA0, 0x60, 0x00);
            _scan.Enabled = false;
            return;
        }

        _media.Text = "Reading the table of contents…";
        _media.ForeColor = Color.Gray;
        try
        {
            var toc = await Task.Run(() => DiscReader.ReadToc(letter.Value));
            _totalSectors = toc.LeadOutLba;
            _media.Text = $"{drive.MediaProfile}: {_totalSectors:N0} sectors " +
                          $"({_totalSectors * 2352.0 / (1024 * 1024 * 1024):0.00} GB raw). Ready to scan.";
            _media.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
            _scan.Enabled = true;
        }
        catch (Exception ex)
        {
            _media.Text = "Could not read the TOC: " + ex.Message;
            _media.ForeColor = Color.FromArgb(0xA0, 0x20, 0x20);
            _scan.Enabled = false;
        }
    }

    private async Task ScanAsync()
    {
        var letter = SelectedLetter();
        if (letter is null || _totalSectors == 0) return;

        var drive = _detected[_drives.SelectedIndex];
        _progress.Value = 0;
        _verdict.Text = "";
        _saveLog.Enabled = false;
        _out.Text = "Scanning…";

        uint sectors = _totalSectors;
        var report = await _runner.RunAsync(cancel =>
        {
            using var dev = new SptiDevice(letter.Value);
            var progress = new Progress<double>(f =>
                BeginInvoke(() => _progress.Value = Math.Clamp((int)(f * 100), 0, 100)));
            return DiscQualityScanner.Scan(dev, sectors, progress: progress, cancel: cancel);
        },
        ex =>
        {
            _out.Text = "Scan failed: " + ex.Message;
            AppLog.WriteException("quality scan", ex);
        });

        if (report is null)
        {
            // Cancelled: a partial scan of a few regions says little about a
            // disc, so there is nothing worth reporting.
            _verdict.Text = "Scan cancelled.";
            _verdict.ForeColor = Color.Gray;
            _out.Text = "Cancelled before the scan finished. A partial sample says little about" +
                        Environment.NewLine +
                        "a disc's condition, so no verdict is offered.";
            _progress.Value = 0;
            return;
        }

        _progress.Value = 100;
        _verdict.Text = report.Verdict;
        _verdict.ForeColor = report.Health switch
        {
            DiscHealth.Excellent => Color.FromArgb(0x20, 0x70, 0x20),
            DiscHealth.Good => Color.FromArgb(0x40, 0x70, 0x20),
            DiscHealth.Marginal => Color.FromArgb(0xA0, 0x60, 0x00),
            DiscHealth.Failing => Color.FromArgb(0xA0, 0x20, 0x20),
            _ => Color.Gray,
        };

        string text = Render(report);
        _out.Text = text;

        // A quality verdict is only meaningful alongside the drive that produced
        // it — drives differ enormously in what they report — so the log carries
        // both.
        var log = new OperationLog("Disc quality scan");
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
            ("Disc size", $"{_totalSectors:N0} sectors"),
            ("Regions", report.Bands.Count),
            ("Sampled", $"{report.TotalSampled:N0} sectors"),
            ("Verdict", report.Health));
        log.Result(text);
        _log = log;
        _saveLog.Enabled = true;

        AppLog.Write($"  quality scan: {report.Health}, {report.TotalWithErrors}/{report.TotalSampled} " +
                     $"sectors with C2, {report.TotalRefused} refused");
        StatusBus.Report($"Disc quality: {report.Health}");
    }

    private static string Render(QualityReport r)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Sampled {r.TotalSampled:N0} sectors across {r.Bands.Count} regions " +
                      $"in {r.Elapsed.TotalSeconds:0.0}s");
        sb.AppendLine();
        sb.AppendLine("  X = would not read   # = needed correction   . = clean");
        sb.AppendLine();
        sb.AppendLine("  Region  LBA range                Sampled  Errors  Unreadable  Profile");
        sb.AppendLine("  ------  -----------------------  -------  ------  ----------  --------------------");

        foreach (var b in r.Bands)
        {
            // A twenty-cell bar showing both kinds of trouble in proportion.
            // The first version painted a solid X whenever a single sector was
            // refused, which made a region that was 97% fine look identical to
            // one that was 80% gone — hiding the very gradation that tells you
            // which parts of a failing disc are still worth imaging.
            const int Cells = 20;
            int refusedCells = (int)Math.Round(b.RefusalRate * Cells);
            int errorCells = (int)Math.Round(b.ErrorRate * Cells);

            // Never round real trouble down to nothing.
            if (b.SectorsRefused > 0 && refusedCells == 0) refusedCells = 1;
            if (b.SectorsWithC2 > 0 && errorCells == 0) errorCells = 1;
            if (refusedCells > Cells) refusedCells = Cells;
            if (refusedCells + errorCells > Cells) errorCells = Cells - refusedCells;

            string bar = new string('X', Math.Max(0, refusedCells))
                       + new string('#', Math.Max(0, errorCells))
                       + new string('.', Math.Max(0, Cells - refusedCells - errorCells));

            sb.AppendLine(
                $"  {b.Index + 1,6}  {b.StartLba,10:N0}–{b.EndLba,-11:N0}  " +
                $"{b.SectorsSampled,7:N0}  {b.SectorsWithC2,6:N0}  {b.SectorsRefused,10:N0}  {bar}");
        }

        sb.AppendLine();
        sb.AppendLine($"  Sectors needing correction : {r.TotalWithErrors:N0} of {r.TotalSampled:N0} " +
                      $"({r.OverallErrorRate:P1})");
        sb.AppendLine($"  Uncorrectable bytes        : {r.TotalBadBytes:N0}");
        sb.AppendLine($"  Sectors that would not read: {r.TotalRefused:N0}");
        sb.AppendLine();

        if (r.Findings.Count > 0)
        {
            sb.AppendLine("Findings:");
            foreach (var f in r.Findings)
            {
                sb.AppendLine();
                foreach (var line in Wrap(f, 74)) sb.AppendLine("  " + line);
            }
            sb.AppendLine();
        }

        sb.AppendLine("Note: this samples the surface rather than reading every sector — the shape");
        sb.AppendLine("of the damage is what matters, and a full read would take hours. A clean");
        sb.AppendLine("scan is good evidence, not a guarantee.");
        return sb.ToString();
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var words = text.Split(' ');
        var line = new StringBuilder();
        foreach (var w in words)
        {
            if (line.Length > 0 && line.Length + 1 + w.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(w);
        }
        if (line.Length > 0) yield return line.ToString();
    }
}