// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Burning;
using DiscForge.Core.Devices;
using DiscForge.Core.Raw;
using DiscForge.Devices;
using DiscForge.Devices.Burning;
using DiscForge.Devices.Reading;
using DiscForge.Devices.Spti;

namespace DiscForge.App.Views;

/// <summary>
/// GUI mirror of <c>dforge prove</c>: the round trip, one verb, one verdict. Burns a .cue to a
/// drive via the direct-SPTI RAW DAO-96 engine (the same path <see cref="BurnView"/> uses for a
/// CUE Write), then reads every track back and verifies it byte-for-byte against the exact image
/// that was burned — main channel, EDC/ECC, and every Q sub-channel frame, not just an MD5.
///
/// Unlike <see cref="DumpCertView"/> (the first GUI-parity pilot, pure hash/JSON work with no live
/// drive), this one genuinely burns a disc — it is not a simulation and it is not undoable. The
/// sequence below is a straight, line-for-line port of <c>ProveCmd</c> in Program.cs (same Core/
/// Devices calls, same order, same golden-image/read-back/compare steps): the underlying burn and
/// read-back primitives are already hardware-proven (RAW DAO ladder rungs 1–7, `read-cdi` on real
/// hardware), so the only genuinely new surface here is this UI layer wiring them together — which
/// is exactly why a same-behavior port was chosen over writing new burn/verify logic from scratch.
/// A confirmation prompt gates Start because, unlike Verify-only or Test (laser-off) in
/// <see cref="BurnView"/>, prove always writes.
/// </summary>
internal sealed class ProveView : UserControl
{
    private readonly TextBox _cuePath = new() { ReadOnly = true, Location = new Point(90, 14), Width = 522, Font = Theme.Ui };
    private readonly Button _cuePick = new() { Text = "Open…", Location = new Point(618, 12), Width = 80, FlatStyle = FlatStyle.System };

    private readonly ComboBox _drive = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(90, 44), Width = 400, Font = Theme.Ui,
    };
    private readonly Button _detect = new() { Text = "Detect drives", Location = new Point(498, 42), Width = 110, FlatStyle = FlatStyle.System };

    private readonly NumericUpDown _speed = new()
    {
        Minimum = 1, Maximum = 24, Value = 1, Width = 60, Location = new Point(90, 74), Font = Theme.Ui,
    };
    private readonly ComboBox _subcode = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(250, 74), Width = 160, Font = Theme.Ui,
    };
    private readonly CheckBox _keepTemp = new()
    {
        Text = "Keep the golden image and per-track read-back captures", AutoSize = true,
        Location = new Point(90, 104), Font = Theme.Ui,
    };
    private readonly CheckBox _writeReport = new()
    {
        Text = "Write an HTML certificate per track", AutoSize = true, Location = new Point(90, 128), Font = Theme.Ui,
    };
    private readonly TextBox _reportPath = new() { ReadOnly = true, Enabled = false, Location = new Point(90, 152), Width = 432, Font = Theme.Ui };
    private readonly Button _reportPick = new() { Text = "…", Enabled = false, Location = new Point(528, 150), Width = 30, FlatStyle = FlatStyle.System };

    private readonly Button _start = new()
    {
        Text = "Prove", Location = new Point(12, 186), Width = 100, Height = 28, FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly ProgressBar _progress = new()
    {
        Location = new Point(124, 189), Size = new Size(524, 22), Minimum = 0, Maximum = 100,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };

    private readonly EventLogView _log = new()
    {
        Location = new Point(12, 224), Size = new Size(712, 236),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
    };

    private string? _cue;
    private IReadOnlyList<DriveCapabilities> _detected = Array.Empty<DriveCapabilities>();

    public ProveView()
    {
        Size = new Size(736, 470);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "CUE:", AutoSize = true, Location = new Point(12, 18), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Drive:", AutoSize = true, Location = new Point(12, 48), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Speed:", AutoSize = true, Location = new Point(12, 78), Font = Theme.Ui });
        Controls.Add(new Label { Text = "x     Subcode:", AutoSize = true, Location = new Point(154, 78), Font = Theme.Ui });
        Controls.Add(new Label
        {
            Text = "Burns <cue> to the drive (RAW DAO-96), reads every track back, and compares each\n" +
                   "one byte-for-byte against the exact image just burned. Consumes one blank disc.",
            AutoSize = true, Location = new Point(12, 480), Font = Theme.Ui, ForeColor = Color.Gray,
        });

        _subcode.Items.AddRange(new object[] { "raw (Interleaved96)", "cooked (Packed96)", "pq (Pq16)" });
        _subcode.SelectedIndex = 0;

        _cuePick.Click += (_, _) => OpenCue();
        _detect.Click += async (_, _) => await DetectAsync();
        _drive.SelectedIndexChanged += (_, _) => UpdateStartEnabled();
        _writeReport.CheckedChanged += (_, _) =>
        {
            _reportPath.Enabled = _reportPick.Enabled = _writeReport.Checked;
            if (_writeReport.Checked && _reportPath.Text.Length == 0) PickReportPath();
        };
        _reportPick.Click += (_, _) => PickReportPath();
        _start.Click += async (_, _) => await StartAsync();

        Controls.Add(_cuePath); Controls.Add(_cuePick);
        Controls.Add(_drive); Controls.Add(_detect);
        Controls.Add(_speed); Controls.Add(_subcode);
        Controls.Add(_keepTemp); Controls.Add(_writeReport);
        Controls.Add(_reportPath); Controls.Add(_reportPick);
        Controls.Add(_start); Controls.Add(_progress);
        Controls.Add(_log);

        _log.Add("Open a .cue, detect a drive, then Prove. This BURNS a disc — no simulation.", EventLogView.Level.Warn);
    }

    private void OpenCue()
    {
        using var dlg = new OpenFileDialog { Filter = "CUE sheets (*.cue)|*.cue|All files (*.*)|*.*" };
        if (AppSettings.LastImageDirectory is { } dir) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog() != DialogResult.OK) return;

        _cue = dlg.FileName;
        _cuePath.Text = dlg.FileName;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(dlg.FileName);
        _log.Add($"CUE: {Path.GetFileName(dlg.FileName)}");
        UpdateStartEnabled();
    }

    private void PickReportPath()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "HTML report (*.html)|*.html", FileName = "prove-report.html",
        };
        if (_cue is not null) dlg.InitialDirectory = Path.GetDirectoryName(_cue);
        if (dlg.ShowDialog() != DialogResult.OK)
        {
            if (_reportPath.Text.Length == 0) _writeReport.Checked = false;
            return;
        }
        _reportPath.Text = dlg.FileName;
    }

    private async Task DetectAsync()
    {
        _log.Add("Detecting drives…");
        try
        {
            _detected = await Task.Run(() => DriveDetector.DetectAll());
            _drive.Items.Clear();
            foreach (var d in _detected.Where(d => d.CdWrite))
                _drive.Items.Add(new DriveItem(d));

            _log.Add(_drive.Items.Count == 0
                    ? "No CD writers detected (raw access usually needs administrator)."
                    : $"{_drive.Items.Count} CD writer(s) detected.",
                _drive.Items.Count == 0 ? EventLogView.Level.Warn : EventLogView.Level.Good);

            if (_drive.Items.Count > 0) _drive.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            _log.Add("Drive detection failed: " + ex.Message, EventLogView.Level.Error);
        }
        UpdateStartEnabled();
    }

    private sealed record DriveItem(DriveCapabilities Drive)
    {
        public override string ToString() => $"{Drive.Vendor} {Drive.Model} ({Drive.DevicePath})";
    }

    private void UpdateStartEnabled() =>
        _start.Enabled = _cue is not null && _drive.SelectedItem is DriveItem;

    private static char? DriveLetterOf(DriveCapabilities drive)
    {
        var path = drive.DevicePath;   // \\.\D:
        int i = path.LastIndexOf(':');
        return i > 0 ? path[i - 1] : null;
    }

    private RawSubcodeForm SelectedSubcodeForm() => _subcode.SelectedIndex switch
    {
        1 => RawSubcodeForm.Packed96,
        2 => RawSubcodeForm.Pq16,
        _ => RawSubcodeForm.Interleaved96,
    };

    private async Task StartAsync()
    {
        if (_cue is null || _drive.SelectedItem is not DriveItem item) return;
        var drive = item.Drive;
        var letter = DriveLetterOf(drive);
        if (letter is null)
        {
            _log.Add($"Could not read a drive letter from '{drive.DevicePath}'.", EventLogView.Level.Error);
            return;
        }

        var confirm = RetroMessageBox.Show(
            $"Prove {Path.GetFileName(_cue)} against {drive.Vendor} {drive.Model} ({drive.DevicePath})?\n\n" +
            "This BURNS a RAW DAO-96 disc — a real write, not a simulation — then reads every track\n" +
            "back and compares it byte-for-byte against the image just burned. It consumes one blank\n" +
            "disc and cannot be undone once started.\n\n" +
            "Insert a blank disc now. Continue?",
            "DiscForge — prove (burn + verify)", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.OK) { _log.Add("Cancelled.", EventLogView.Level.Warn); return; }

        _log.Clear();
        _progress.Value = 0;
        _start.Enabled = false;
        try { await RunAsync(_cue, letter.Value, drive); }
        finally { UpdateStartEnabled(); }
    }

    /// <summary>
    /// Same sequence as ProveCmd in Program.cs: compose the golden image, burn it (RAW DAO-96,
    /// direct SPTI), then read every track back and compare against the golden — one verdict at
    /// the end. Kept as a line-for-line port rather than a redesign, since the CLI path is the one
    /// that's actually been exercised against real hardware.
    /// </summary>
    private async Task RunAsync(string cuePath, char letter, DriveCapabilities caps)
    {
        int speed = (int)_speed.Value;
        var form = SelectedSubcodeForm();
        bool keepTemp = _keepTemp.Checked;
        string? reportBase = _writeReport.Checked && _reportPath.Text.Length > 0 ? _reportPath.Text : null;

        string dir = Path.GetDirectoryName(cuePath) is { Length: > 0 } d ? d : ".";
        string baseName = Path.GetFileNameWithoutExtension(cuePath);
        string goldenPath = Path.Combine(dir, baseName + ".prove-golden.img");

        try
        {
            await Task.Run(() =>
            {
                // ---- 1. compose the golden image --------------------------------------
                _log.Add("[1] composing golden image…");
                using (var layoutForGolden = DiscLayout.FromCueFile(cuePath))
                {
                    _log.Add($"    {layoutForGolden.Tracks.Count} track(s), " +
                             $"{RawImageGenerator.ProgramSectors(layoutForGolden):N0} program sectors");
                    using var goldenOut = File.Create(goldenPath);
                    RawImageGenerator.Generate(layoutForGolden, form, goldenOut);
                }
                _log.Add($"    wrote {Path.GetFileName(goldenPath)}");

                // ---- 2. burn ------------------------------------------------------------
                _log.Add($"[2] burning (RAW DAO-96, direct SPTI, speed {speed}x)…");
                using var layoutForBurn = DiscLayout.FromCueFile(cuePath);
                var burnProgress = new Progress<BurnProgress>(p =>
                {
                    _progress.Value = Math.Clamp((int)(p.Fraction * 100), 0, 100);
                    StatusBus.Report($"[{p.Phase}] {p.Fraction * 100:0.0}%  {p.Detail}");
                });
                SptiRawDaoBurnEngine.Burn(letter, layoutForBurn, burnProgress, simulate: false,
                    writeSpeedMultiplier: speed);
                _log.Add("    burn complete.", EventLogView.Level.Good);

                // ---- 3. read every track back and verify it against the golden ----------
                using var dev = new SptiDevice(letter);
                var toc = DiscReader.ReadToc(dev);
                var verdicts = new List<(int Track, bool Ok, string Summary)>();
                foreach (var t in toc.Tracks)
                {
                    _log.Add($"[3] track {t.Number} ({(t.IsData ? "data" : "audio")}): reading back…");
                    var fieldSel = t.IsData ? RawDiscReader.FieldSelect.Data : RawDiscReader.FieldSelect.Audio;
                    string rbPath = Path.Combine(dir, $"{baseName}.prove-track{t.Number:D2}.bin");
                    string? readNote = null;
                    try
                    {
                        using var fs = File.Create(rbPath);
                        RawDiscReader.Read(dev, (int)t.StartLba, t.LengthSectors, fs, null, fieldSel);
                    }
                    catch (IOException ex)
                    {
                        // A data track immediately followed by an audio track carries a few
                        // sectors of audio-format pregap the drive refuses in forced (single)
                        // field mode. RawDiscReader.Read already flushed everything it COULD
                        // read before throwing — a short, honest partial capture at the track
                        // boundary, not a burn defect. Compare() runs with partial: true for
                        // exactly this reason. Mirrors ProveCmd's own handling verbatim.
                        readNote = $"read stopped at the track boundary ({ex.Message})";
                    }
                    if (readNote is not null) _log.Add($"    note: {readNote}", EventLogView.Level.Warn);

                    RawReadbackCompare.Report r;
                    long goldenLen, rbLen;
                    using (var golden = File.OpenRead(goldenPath))
                    using (var readback = File.OpenRead(rbPath))
                    {
                        goldenLen = golden.Length; rbLen = readback.Length;
                        r = RawReadbackCompare.Compare(golden, readback, partial: true);
                    }
                    bool ok = r.Result != RawReadbackCompare.Grade.Fail;
                    verdicts.Add((t.Number, ok, r.Summary));
                    _log.Add($"    {(ok ? "OK" : "FAIL")} — {r.Summary}", ok ? EventLogView.Level.Good : EventLogView.Level.Error);

                    if (reportBase is not null)
                    {
                        string trackReport = Path.Combine(
                            Path.GetDirectoryName(reportBase) is { Length: > 0 } rd ? rd : ".",
                            Path.GetFileNameWithoutExtension(reportBase) + $"-track{t.Number:D2}" + Path.GetExtension(reportBase));
                        File.WriteAllText(trackReport, RawReadbackReport.Html(
                            r, Path.GetFileName(goldenPath), Path.GetFileName(rbPath), goldenLen, rbLen,
                            DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")));
                        _log.Add($"    wrote {Path.GetFileName(trackReport)}");
                    }

                    if (!keepTemp) File.Delete(rbPath);
                }
                if (!keepTemp) File.Delete(goldenPath);

                _progress.Value = 100;
                bool proven = verdicts.Count > 0 && verdicts.All(v => v.Ok);
                _log.Add(proven
                        ? $"=== PROVEN — all {verdicts.Count} track(s) verified byte-for-byte. ==="
                        : $"=== FAILED — {verdicts.Count(v => !v.Ok)}/{verdicts.Count} track(s) did not verify. ===",
                    proven ? EventLogView.Level.Good : EventLogView.Level.Error);
                foreach (var v in verdicts.Where(v => !v.Ok))
                    _log.Add($"  track {v.Track}: {v.Summary}", EventLogView.Level.Error);
            });
        }
        catch (Exception ex)
        {
            _log.Add("Failed: " + ex.Message, EventLogView.Level.Error);
            AppLog.WriteException("prove", ex);
        }
    }
}
