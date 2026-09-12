// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using DiscForge.Core.Forensics;

namespace DiscForge.App.Views;

/// <summary>
/// GUI mirror of <c>dforge disc-actuary</c>: a quality scan says how a disc is TODAY; the actuary
/// keeps every scan as a time series and fits a first-order decay model per disc, so it can say how
/// long a disc has LEFT — and, across a whole collection, which discs to re-dump first because
/// they're dying fastest. Prioritisation only: it schedules rescues, it performs none.
///
/// Same lower-risk shape as <see cref="DumpCertView"/>, <see cref="PressingDnaView"/>, and
/// <see cref="DriveDossierView"/> — pure local JSON + math, no live drive at all (unlike those
/// three, this one doesn't even read drive capabilities; the "drive" a scan records is just a free
/// label, exactly as the CLI treats it). One JSON file per disc under
/// <c>%AppData%\DiscForge\actuary</c> by default, same as <see cref="DiscActuary.LoadOrNew"/>/
/// <see cref="DiscActuary.PathFor"/> already store it for the CLI.
/// </summary>
internal sealed class DiscActuaryView : UserControl
{
    private readonly TextBox _discId = new() { Location = new Point(80, 14), Width = 200, Font = Theme.Ui };
    private readonly TextBox _title = new() { Location = new Point(390, 14), Width = 220, Font = Theme.Ui };

    private readonly TextBox _dir = new() { Location = new Point(90, 44), Width = 522, Font = Theme.Ui };
    private readonly Button _dirPick = new() { Text = "…", Location = new Point(618, 42), Width = 30, FlatStyle = FlatStyle.System };

    private readonly Button _load = new()
    {
        Text = "Load / Assess", Location = new Point(12, 74), Width = 120, Height = 26, FlatStyle = FlatStyle.System, Enabled = false,
    };

    private readonly CheckBox _importScan = new()
    {
        Text = "Import from a scan file instead of typing tier1/tier2/uncorrectable",
        AutoSize = true, Location = new Point(12, 130), Font = Theme.Ui,
    };

    private readonly NumericUpDown _tier1 = new() { Minimum = 0, Maximum = 999_999, Location = new Point(150, 152), Width = 70, Font = Theme.Ui };
    private readonly NumericUpDown _tier2 = new() { Minimum = 0, Maximum = 999_999, Location = new Point(370, 152), Width = 70, Font = Theme.Ui };
    private readonly NumericUpDown _cu = new() { Minimum = 0, Maximum = 999_999, Location = new Point(590, 152), Width = 70, Font = Theme.Ui };

    private readonly TextBox _scanFile = new() { ReadOnly = true, Enabled = false, Location = new Point(90, 178), Width = 430, Font = Theme.Ui };
    private readonly Button _scanPick = new() { Text = "…", Enabled = false, Location = new Point(528, 176), Width = 30, FlatStyle = FlatStyle.System };

    private readonly TextBox _drive = new() { Location = new Point(70, 206), Width = 150, Font = Theme.Ui };
    private readonly TextBox _when = new() { Location = new Point(400, 206), Width = 180, Font = Theme.Ui };

    private readonly Button _record = new()
    {
        Text = "Record Scan", Location = new Point(12, 234), Width = 120, Height = 26, FlatStyle = FlatStyle.System, Enabled = false,
    };

    private readonly CheckBox _useEnv = new()
    {
        Text = "Specify storage environment:", AutoSize = true, Location = new Point(12, 292), Font = Theme.Ui,
    };
    private readonly NumericUpDown _tempC = new()
    {
        Minimum = -50, Maximum = 80, DecimalPlaces = 1, Increment = 0.5m, Value = 25,
        Location = new Point(290, 290), Width = 70, Font = Theme.Ui, Enabled = false,
    };
    private readonly NumericUpDown _rh = new()
    {
        Minimum = 0, Maximum = 100, DecimalPlaces = 0, Value = 50,
        Location = new Point(430, 290), Width = 70, Font = Theme.Ui, Enabled = false,
    };
    private readonly NumericUpDown _threshold = new()
    {
        Minimum = 1, Maximum = 100_000, Value = (decimal)RotKinetics.DefaultThreshold,
        Location = new Point(200, 320), Width = 90, Font = Theme.Ui,
    };

    private readonly Button _rankCollection = new()
    {
        Text = "Rank Whole Collection", Location = new Point(12, 350), Width = 170, Height = 26, FlatStyle = FlatStyle.System,
    };

    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Location = new Point(12, 386), Size = new Size(712, 208),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        Font = Theme.Mono, BackColor = Color.White,
    };

    public DiscActuaryView()
    {
        Size = new Size(736, 606);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Disc ID:", AutoSize = true, Location = new Point(12, 18), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Title (optional):", AutoSize = true, Location = new Point(300, 18), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Store folder:", AutoSize = true, Location = new Point(12, 48), Font = Theme.Ui });

        Controls.Add(GroupLabel("Record a new scan (optional)", new Point(12, 108)));
        Controls.Add(new Label { Text = "Tier1 (C1/PIE) max:", AutoSize = true, Location = new Point(12, 154), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Tier2 (C2/PIF) max:", AutoSize = true, Location = new Point(232, 154), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Uncorrectable (CU/POF):", AutoSize = true, Location = new Point(452, 154), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Scan file:", AutoSize = true, Location = new Point(12, 180), Font = Theme.Ui, ForeColor = Color.Gray });
        Controls.Add(new Label { Text = "Drive:", AutoSize = true, Location = new Point(12, 208), Font = Theme.Ui });
        Controls.Add(new Label { Text = "When (ISO, optional):", AutoSize = true, Location = new Point(280, 208), Font = Theme.Ui });

        Controls.Add(GroupLabel("Trend fit (optional)", new Point(12, 272)));
        Controls.Add(new Label { Text = "°C", AutoSize = true, Location = new Point(364, 292), Font = Theme.Ui });
        Controls.Add(new Label { Text = "% RH", AutoSize = true, Location = new Point(504, 292), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Rot threshold (default 220):", AutoSize = true, Location = new Point(12, 322), Font = Theme.Ui });

        _dir.Text = DiscActuaryStoreDefaultDirectory();

        _discId.TextChanged += (_, _) => UpdateButtonsEnabled();
        _dirPick.Click += (_, _) => PickDir();
        _load.Click += (_, _) => DoLoad();
        _importScan.CheckedChanged += (_, _) =>
        {
            bool import = _importScan.Checked;
            _tier1.Enabled = _tier2.Enabled = _cu.Enabled = !import;
            _scanFile.Enabled = _scanPick.Enabled = import;
        };
        _scanPick.Click += (_, _) => PickScanFile();
        _record.Click += (_, _) => DoRecord();
        _useEnv.CheckedChanged += (_, _) => _tempC.Enabled = _rh.Enabled = _useEnv.Checked;
        _rankCollection.Click += (_, _) => DoRankCollection();

        Controls.Add(_discId); Controls.Add(_title);
        Controls.Add(_dir); Controls.Add(_dirPick);
        Controls.Add(_load);
        Controls.Add(_importScan);
        Controls.Add(_tier1); Controls.Add(_tier2); Controls.Add(_cu);
        Controls.Add(_scanFile); Controls.Add(_scanPick);
        Controls.Add(_drive); Controls.Add(_when);
        Controls.Add(_record);
        Controls.Add(_useEnv); Controls.Add(_tempC); Controls.Add(_rh); Controls.Add(_threshold);
        Controls.Add(_rankCollection);
        Controls.Add(_log);

        _log.Text = "Give a disc id (the disc-genome ShortId, or any label you'll keep using), then " +
                    "Load / Assess. Any label you'll keep using is fine.";
    }

    private static Label GroupLabel(string text, Point at) => new()
    {
        Text = text, AutoSize = true, Location = at, Font = Theme.UiBold, ForeColor = Theme.Accent,
    };

    /// <summary>Same default the CLI falls back to when <c>--dir</c> is not given.</summary>
    private static string DiscActuaryStoreDefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiscForge", "actuary");

    private void PickDir()
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = _dir.Text, Description = "Disc actuary store folder" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _dir.Text = dlg.SelectedPath;
    }

    private void PickScanFile()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Quality-scan files (*.csv;*.txt;*.xml;*.log)|*.csv;*.txt;*.xml;*.log|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _scanFile.Text = dlg.FileName;
    }

    private void UpdateButtonsEnabled()
    {
        bool ok = _discId.Text.Trim().Length > 0;
        _load.Enabled = ok;
        _record.Enabled = ok;
    }

    private string CurrentDir() => _dir.Text.Trim().Length > 0 ? _dir.Text.Trim() : DiscActuaryStoreDefaultDirectory();

    private (StorageEnvironment? env, double threshold) CurrentFitOptions()
    {
        StorageEnvironment? env = _useEnv.Checked ? new StorageEnvironment((double)_tempC.Value, (double)_rh.Value) : null;
        double threshold = (double)_threshold.Value;
        return (env, threshold);
    }

    private void DoLoad()
    {
        string discId = _discId.Text.Trim();
        if (discId.Length == 0) return;
        string dir = CurrentDir();
        string? title = _title.Text.Trim().Length > 0 ? _title.Text.Trim() : null;

        try
        {
            var history = DiscActuary.LoadOrNew(dir, discId, title);
            var (env, threshold) = CurrentFitOptions();
            var verdict = DiscActuary.Assess(history, env, threshold);
            RenderVerdict(verdict, dir, discId);
            AppLog.Write($"disc-actuary {discId}");
        }
        catch (Exception ex)
        {
            _log.Text = "Load / Assess failed: " + ex.Message;
            AppLog.WriteException("disc-actuary load", ex);
        }
    }

    private void DoRecord()
    {
        string discId = _discId.Text.Trim();
        if (discId.Length == 0) return;
        string dir = CurrentDir();
        string? title = _title.Text.Trim().Length > 0 ? _title.Text.Trim() : null;

        try
        {
            var history = DiscActuary.LoadOrNew(dir, discId, title);

            DateTimeOffset when = DateTimeOffset.UtcNow;
            if (_when.Text.Trim().Length > 0)
                when = DateTimeOffset.Parse(_when.Text.Trim(), CultureInfo.InvariantCulture);

            double t1, t2 = 0, cu = 0;
            string? drive = _drive.Text.Trim().Length > 0 ? _drive.Text.Trim() : null;
            string source = "manual";

            if (_importScan.Checked)
            {
                if (_scanFile.Text.Length == 0 || !File.Exists(_scanFile.Text))
                {
                    _log.Text = "Pick a readable scan file first, or untick \"Import from a scan file\".";
                    return;
                }
                var qs = QualityScanImport.Parse(File.ReadAllText(_scanFile.Text));
                var maxes = qs.Rows.Select(r => r.ToSample(qs.Family)).ToList();
                t1 = maxes.Count > 0 ? maxes.Max(s => s.C1) : 0;
                t2 = maxes.Count > 0 ? maxes.Max(s => s.C2) : 0;
                cu = maxes.Count > 0 ? maxes.Max(s => s.Cu) : 0;
                if (qs.ScannedAt is { } sa) when = sa;
                drive ??= qs.Drive;
                source = Path.GetFileName(_scanFile.Text);
            }
            else
            {
                t1 = (double)_tier1.Value;
                t2 = (double)_tier2.Value;
                cu = (double)_cu.Value;
            }

            history = history.Append(new ActuaryScan(
                when.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), t1, t2, cu, drive, source));
            history.Save(DiscActuary.PathFor(dir, discId));

            var (env, threshold) = CurrentFitOptions();
            var verdict = DiscActuary.Assess(history, env, threshold);

            var sb = new StringBuilder();
            sb.AppendLine($"Recorded scan #{history.Scans.Count} for {discId}: tier1={t1:0}, tier2={t2:0}, cu={cu:0}.");
            sb.AppendLine();
            AppendVerdict(sb, verdict, dir, discId);
            _log.Text = sb.ToString();

            StatusBus.Report($"Disc actuary: recorded scan #{history.Scans.Count} for {discId}");
            AppLog.Write($"disc-actuary {discId} --record tier1={t1:0} tier2={t2:0} cu={cu:0}");
        }
        catch (Exception ex)
        {
            _log.Text = "Record scan failed: " + ex.Message;
            AppLog.WriteException("disc-actuary record", ex);
        }
    }

    private void DoRankCollection()
    {
        string dir = CurrentDir();
        try
        {
            var all = DiscActuary.LoadAll(dir);
            if (all.Count == 0)
            {
                _log.Text = $"No histories under '{dir}' yet — record scans first.";
                return;
            }
            var (env, threshold) = CurrentFitOptions();
            var ranked = DiscActuary.Rank(all, env, threshold);
            _log.Text = DiscActuary.RenderTriage(ranked);
            AppLog.Write($"disc-actuary --collection ({all.Count} disc(s))");
        }
        catch (Exception ex)
        {
            _log.Text = "Rank collection failed: " + ex.Message;
            AppLog.WriteException("disc-actuary collection", ex);
        }
    }

    private void RenderVerdict(ActuaryVerdict verdict, string dir, string discId)
    {
        var sb = new StringBuilder();
        AppendVerdict(sb, verdict, dir, discId);
        _log.Text = sb.ToString();
    }

    private static void AppendVerdict(StringBuilder sb, ActuaryVerdict verdict, string dir, string discId)
    {
        sb.AppendLine($"{verdict.Title ?? discId}: {verdict.Headline}");
        if (verdict.Kinetics is { } k)
        {
            sb.AppendLine($"  trend    : {k.GrowthPerYear:P0}/yr over {verdict.ScanCount} scans (R²={k.RSquared:0.00})");
            if (k.ThresholdDate is { } td)
                sb.AppendLine($"  crosses  : ~{td:yyyy-MM}" + (k.Band is { } b ? $" (band {b.Early:yyyy-MM}..{b.Late:yyyy-MM})" : ""));
            if (Math.Abs(k.EnvAccelFactor - 1.0) > 0.01)
                sb.AppendLine($"  storage  : x{k.EnvAccelFactor:0.00} vs 25C/50%RH reference");
        }
        sb.AppendLine($"  file     : {DiscActuary.PathFor(dir, discId)}");
    }
}
