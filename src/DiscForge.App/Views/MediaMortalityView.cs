// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Forensics;

namespace DiscForge.App.Views;

/// <summary>
/// GUI mirror of <c>dforge media-mortality</c>: a federated (not merely aggregated) model of how fast a
/// cohort of discs decays. Fold in one disc's own <see cref="DiscActuary"/>/<see cref="RotKinetics"/> fit
/// (growth rate + sample count — nothing else, nothing identifying) with Observe, merge any two
/// contributors' models exactly with Merge, and get a community estimate once
/// <see cref="MediaMortality.MinContributorsToReport"/> independent discs back it. Pure local-file
/// analysis, no live drive — the model file itself never carries a disc id, title, or scan history.
/// </summary>
internal sealed class MediaMortalityView : UserControl
{
    private readonly TextBox _modelPath = new() { ReadOnly = true, Location = new Point(90, 14), Width = 420, Font = Theme.Ui };
    private readonly Button _open = new() { Text = "Open…", Location = new Point(516, 12), Width = 80, FlatStyle = FlatStyle.System };
    private readonly Button _new = new() { Text = "New…", Location = new Point(600, 12), Width = 70, FlatStyle = FlatStyle.System };

    private readonly TextBox _cohort = new() { Location = new Point(90, 48), Width = 250, Font = Theme.Ui };
    private readonly NumericUpDown _growthPct = new()
    {
        DecimalPlaces = 3, Minimum = -100, Maximum = 1000, Increment = 0.1m,
        Location = new Point(360, 48), Width = 90, Font = Theme.Ui,
    };
    private readonly NumericUpDown _samples = new()
    {
        Minimum = 2, Maximum = 10000, Value = 3,
        Location = new Point(470, 48), Width = 70, Font = Theme.Ui,
    };
    private readonly Button _observe = new()
    {
        Text = "Observe", Location = new Point(560, 46), Width = 90, Height = 26, FlatStyle = FlatStyle.System, Enabled = false,
    };

    private readonly Button _mergeWith = new()
    {
        Text = "Merge With…", Location = new Point(12, 82), Width = 108, Height = 26, FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Button _estimate = new()
    {
        Text = "Estimate", Location = new Point(128, 82), Width = 90, Height = 26, FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Button _showAll = new()
    {
        Text = "Show All", Location = new Point(226, 82), Width = 90, Height = 26, FlatStyle = FlatStyle.System, Enabled = false,
    };

    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Location = new Point(12, 120), Size = new Size(712, 340),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        Font = Theme.Mono, BackColor = Color.White,
    };

    private string? _modelFile;

    public MediaMortalityView()
    {
        Size = new Size(736, 500);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Model:", AutoSize = true, Location = new Point(12, 18), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Cohort:", AutoSize = true, Location = new Point(12, 52), Font = Theme.Ui });
        Controls.Add(new Label { Text = "growth %/yr", AutoSize = true, Location = new Point(360, 32), Font = Theme.Small, ForeColor = Color.Gray });
        Controls.Add(new Label { Text = "samples", AutoSize = true, Location = new Point(470, 32), Font = Theme.Small, ForeColor = Color.Gray });

        _open.Click += (_, _) => DoOpen();
        _new.Click += (_, _) => DoNew();
        _observe.Click += (_, _) => DoObserve();
        _mergeWith.Click += (_, _) => DoMerge();
        _estimate.Click += (_, _) => DoEstimate();
        _showAll.Click += (_, _) => DoShowAll();

        Controls.AddRange(new Control[]
        {
            _modelPath, _open, _new, _cohort, _growthPct, _samples, _observe,
            _mergeWith, _estimate, _showAll, _log,
        });

        _log.Text =
            "Open an existing media-mortality model.json, or start a New one.\r\n\r\n" +
            "A cohort is any taxonomy you choose — a manufacturer/mold-SID code, or a media type + brand. " +
            "Observe folds in one disc's own rot-kinetics fit (growth %/yr and sample count from Disc " +
            "Actuary) as two numbers, nothing identifying. Merge With combines your model exactly with " +
            "anyone else's — order-independent, no coordinator needed. Estimate reports a cohort's mean " +
            $"decay rate only once {MediaMortality.MinContributorsToReport}+ independent discs back it — " +
            "a privacy floor, not an accuracy one. Show All lists every cohort in the model.";
    }

    private void DoOpen()
    {
        using var dlg = new OpenFileDialog { Filter = "Media-mortality model (*.json)|*.json|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        LoadModelFile(dlg.FileName);
    }

    private void DoNew()
    {
        using var dlg = new SaveFileDialog { Filter = "Media-mortality model (*.json)|*.json", FileName = "media-mortality.json" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            MediaMortality.Save(new MediaMortalityModel(), dlg.FileName);
            LoadModelFile(dlg.FileName);
            _log.Text = $"Started {Path.GetFileName(dlg.FileName)}: an empty media-mortality model.";
            AppLog.Write($"media-mortality new {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            _log.Text = "Could not start a new model: " + ex.Message;
            AppLog.WriteException("media-mortality new", ex);
        }
    }

    private void LoadModelFile(string path)
    {
        _modelFile = path;
        _modelPath.Text = path;
        _observe.Enabled = true;
        _mergeWith.Enabled = true;
        _estimate.Enabled = true;
        _showAll.Enabled = true;
        _log.Text = $"Loaded {Path.GetFileName(path)}. Type a cohort and Observe, or Show All to see what's already in it.";
    }

    private void DoObserve()
    {
        if (_modelFile is null) return;
        string cohort = _cohort.Text.Trim();
        if (cohort.Length == 0) { _log.Text = "Type a cohort name first."; return; }
        try
        {
            var model = File.Exists(_modelFile) ? MediaMortality.Load(_modelFile) : new MediaMortalityModel();
            double growthPerYear = (double)_growthPct.Value / 100.0;
            int samples = (int)_samples.Value;
            MediaMortality.Observe(model, cohort, new CohortObservation(growthPerYear, samples));
            MediaMortality.Save(model, _modelFile);

            var s = model.Cohorts[cohort];
            _log.Text = $"Observed into {Path.GetFileName(_modelFile)}: cohort '{cohort}' now has " +
                       $"{s.ContributorCount} contributor(s), mean growth {s.MeanGrowthPerYear:0.####}/yr.";
            StatusBus.Report($"Media mortality: observed '{cohort}' ({s.ContributorCount} contributor(s))");
            AppLog.Write($"media-mortality observe {cohort} -> {Path.GetFileName(_modelFile)}");
        }
        catch (Exception ex)
        {
            _log.Text = "Observe failed: " + ex.Message;
            AppLog.WriteException("media-mortality observe", ex);
        }
    }

    private void DoMerge()
    {
        if (_modelFile is null) return;
        using var dlg = new OpenFileDialog { Filter = "Media-mortality model (*.json)|*.json|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            var a = MediaMortality.Load(_modelFile);
            var b = MediaMortality.Load(dlg.FileName);
            var merged = MediaMortality.Merge(a, b);
            MediaMortality.Save(merged, _modelFile);
            _log.Text = $"Merged {Path.GetFileName(dlg.FileName)} into {Path.GetFileName(_modelFile)}: " +
                       $"{merged.Cohorts.Count} cohort(s) total.";
            StatusBus.Report($"Media mortality: merged into {Path.GetFileName(_modelFile)} ({merged.Cohorts.Count} cohort(s))");
            AppLog.Write($"media-mortality merge {Path.GetFileName(dlg.FileName)} -> {Path.GetFileName(_modelFile)}");
        }
        catch (Exception ex)
        {
            _log.Text = "Merge failed: " + ex.Message;
            AppLog.WriteException("media-mortality merge", ex);
        }
    }

    private void DoEstimate()
    {
        if (_modelFile is null) return;
        string cohort = _cohort.Text.Trim();
        if (cohort.Length == 0) { _log.Text = "Type a cohort name first."; return; }
        try
        {
            var model = MediaMortality.Load(_modelFile);
            var estimate = MediaMortality.Estimate(model, cohort);
            if (estimate is null)
            {
                int have = model.Cohorts.TryGetValue(cohort, out var s) ? s.ContributorCount : 0;
                _log.Text = $"No estimate for '{cohort}': {have} contributor(s), need " +
                           $"{MediaMortality.MinContributorsToReport} (privacy floor).";
            }
            else
            {
                _log.Text = estimate.Summary();
            }
            AppLog.Write($"media-mortality estimate {cohort}: {(estimate is null ? "below floor" : "ok")}");
        }
        catch (Exception ex)
        {
            _log.Text = "Estimate failed: " + ex.Message;
            AppLog.WriteException("media-mortality estimate", ex);
        }
    }

    private void DoShowAll()
    {
        if (_modelFile is null) return;
        try
        {
            var model = MediaMortality.Load(_modelFile);
            var sb = new System.Text.StringBuilder();
            foreach (var (key, s) in model.Cohorts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                sb.AppendLine($"  {key}: {s.ContributorCount} contributor(s), mean {s.MeanGrowthPerYear:0.####}/yr, " +
                              $"stddev {s.StdDev:0.####}" +
                              (s.ContributorCount < MediaMortality.MinContributorsToReport
                                  ? "  (below privacy floor — not reportable via Estimate)" : ""));
            sb.AppendLine($"{model.Cohorts.Count} cohort(s) total.");
            _log.Text = sb.ToString();
            AppLog.Write($"media-mortality show {Path.GetFileName(_modelFile)}");
        }
        catch (Exception ex)
        {
            _log.Text = "Show failed: " + ex.Message;
            AppLog.WriteException("media-mortality show", ex);
        }
    }
}
