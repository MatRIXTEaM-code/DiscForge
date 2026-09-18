// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Devices;
using DiscForge.Devices;

namespace DiscForge.App.Views;

/// <summary>
/// GUI mirror of <c>dforge drive-dossier</c>: the institutional memory a session's terminal
/// scrollback used to eat — a per-drive dossier that accumulates observed behaviour (mute
/// signatures, C2 wolf-cries, a confirmed AccurateRip offset, overread reach) across every
/// operation, distilled into warnings the next dump can see BEFORE it repeats a hard lesson.
///
/// Same lower-risk shape as <see cref="DumpCertView"/> and <see cref="PressingDnaView"/>: drive
/// DETECTION (optional, to pre-fill vendor/model) is the only live-hardware touch, and it is
/// read-only (<see cref="DriveDetector.Detect"/>/<see cref="DriveDetector.DetectAll"/> only —
/// the same calls <see cref="BurnView"/> and <see cref="ProveView"/> already use for their own
/// destination lists). Everything else — <see cref="DriveDossierStore"/>,
/// <see cref="DriveDossier"/>, <see cref="DriveKnowledgeBase"/> — is local JSON under
/// <c>%AppData%\DiscForge\drives</c> by default, exactly like the CLI.
/// </summary>
internal sealed class DriveDossierView : UserControl
{
    private readonly ComboBox _drive = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(90, 14), Width = 400, Font = Theme.Ui,
    };
    private readonly Button _detect = new() { Text = "Detect drives", Location = new Point(498, 12), Width = 110, FlatStyle = FlatStyle.System };

    private readonly TextBox _vendor = new() { Location = new Point(90, 44), Width = 190, Font = Theme.Ui };
    private readonly TextBox _model = new() { Location = new Point(350, 44), Width = 298, Font = Theme.Ui };

    private readonly TextBox _dir = new() { Location = new Point(90, 74), Width = 522, Font = Theme.Ui };
    private readonly Button _dirPick = new() { Text = "…", Location = new Point(618, 72), Width = 30, FlatStyle = FlatStyle.System };

    private readonly Button _load = new()
    {
        Text = "Load Dossier", Location = new Point(12, 106), Width = 120, Height = 26, FlatStyle = FlatStyle.System, Enabled = false,
    };

    private readonly ComboBox _category = new()
    {
        DropDownStyle = ComboBoxStyle.DropDown, Location = new Point(120, 144), Width = 160, Font = Theme.Ui,
    };
    private readonly TextBox _detail = new() { Location = new Point(292, 144), Width = 250, Font = Theme.Ui };
    private readonly TextBox _value = new() { Location = new Point(554, 144), Width = 90, Font = Theme.Ui };
    private readonly Button _observe = new()
    {
        Text = "Add Observation", Location = new Point(12, 172), Width = 130, Height = 26, FlatStyle = FlatStyle.System, Enabled = false,
    };

    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Location = new Point(12, 208), Size = new Size(712, 244),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        Font = Theme.Mono, BackColor = Color.White,
    };

    private string? _firmware;
    private IReadOnlyList<DriveCapabilities> _detected = Array.Empty<DriveCapabilities>();

    public DriveDossierView()
    {
        Size = new Size(736, 464);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Drive:", AutoSize = true, Location = new Point(12, 18), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Vendor:", AutoSize = true, Location = new Point(12, 48), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Model:", AutoSize = true, Location = new Point(292, 48), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Store folder:", AutoSize = true, Location = new Point(12, 78), Font = Theme.Ui });
        Controls.Add(GroupLabel("Add an observation (optional)", new Point(12, 128)));
        Controls.Add(new Label { Text = "Category:", AutoSize = true, Location = new Point(12, 148), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Detail:", AutoSize = true, Location = new Point(292, 128), Font = Theme.Ui, ForeColor = Color.Gray });
        Controls.Add(new Label { Text = "Value (optional, numeric):", AutoSize = true, Location = new Point(554, 128), Font = Theme.Ui, ForeColor = Color.Gray });

        _dir.Text = DriveDossierStore.DefaultDirectory;
        _category.Items.AddRange(new object[]
        {
            DriveDossier.CategoryMute, DriveDossier.CategoryC2FirstSector, DriveDossier.CategoryOffset,
            DriveDossier.CategoryLeadOutOverread, DriveDossier.CategoryLeadInReach,
        });

        _detect.Click += async (_, _) => await DetectAsync();
        _drive.SelectedIndexChanged += (_, _) => OnDriveSelected();
        _dirPick.Click += (_, _) => PickDir();
        foreach (var tb in new[] { _vendor, _model }) tb.TextChanged += (_, _) => UpdateLoadEnabled();
        _load.Click += (_, _) => DoLoad();
        _observe.Click += (_, _) => DoObserve();

        Controls.Add(_drive); Controls.Add(_detect);
        Controls.Add(_vendor); Controls.Add(_model);
        Controls.Add(_dir); Controls.Add(_dirPick);
        Controls.Add(_load);
        Controls.Add(_category); Controls.Add(_detail); Controls.Add(_value); Controls.Add(_observe);
        Controls.Add(_log);

        _log.Text = "Detect a drive, or type its vendor/model directly, then Load Dossier.";
    }

    private static Label GroupLabel(string text, Point at) => new()
    {
        Text = text, AutoSize = true, Location = at, Font = Theme.UiBold, ForeColor = Theme.Accent,
    };

    private async Task DetectAsync()
    {
        try
        {
            _detected = await Task.Run(() => DriveDetector.DetectAll());
            _drive.Items.Clear();
            foreach (var d in _detected) _drive.Items.Add(new DriveItem(d));
            if (_drive.Items.Count > 0) _drive.SelectedIndex = 0;
            else _log.Text = "No optical drives detected (raw access usually needs administrator). " +
                              "You can still type a vendor/model directly.";
        }
        catch (Exception ex)
        {
            _log.Text = "Drive detection failed: " + ex.Message;
            AppLog.WriteException("drive-dossier detect", ex);
        }
    }

    private sealed record DriveItem(DriveCapabilities Drive)
    {
        public override string ToString() => $"{Drive.Vendor} {Drive.Model} ({Drive.DevicePath})";
    }

    private void OnDriveSelected()
    {
        if (_drive.SelectedItem is not DriveItem item) return;
        _vendor.Text = item.Drive.Vendor;
        _model.Text = item.Drive.Model;
        _firmware = item.Drive.FirmwareRevision;
    }

    private void PickDir()
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = _dir.Text, Description = "Dossier store folder" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _dir.Text = dlg.SelectedPath;
    }

    private void UpdateLoadEnabled() =>
        _load.Enabled = _vendor.Text.Trim().Length > 0 && _model.Text.Trim().Length > 0;

    private DriveDossier? _current;

    private void DoLoad()
    {
        string vendor = _vendor.Text.Trim(), model = _model.Text.Trim();
        if (vendor.Length == 0 || model.Length == 0) return;
        string dir = _dir.Text.Trim().Length > 0 ? _dir.Text.Trim() : DriveDossierStore.DefaultDirectory;

        try
        {
            _current = DriveDossierStore.LoadOrNew(dir, vendor, model, _firmware);
            RenderCurrent(dir, vendor, model);
            _observe.Enabled = true;
            AppLog.Write($"drive-dossier {vendor} {model}");
        }
        catch (Exception ex)
        {
            _log.Text = "Load failed: " + ex.Message;
            _observe.Enabled = false;
            AppLog.WriteException("drive-dossier load", ex);
        }
    }

    private void DoObserve()
    {
        if (_current is null) return;
        string category = _category.Text.Trim();
        string detail = _detail.Text.Trim();
        if (category.Length == 0 || detail.Length == 0)
        {
            _log.Text = "Give both a category and a detail before adding an observation.";
            return;
        }
        long? value = null;
        if (_value.Text.Trim().Length > 0)
        {
            if (!long.TryParse(_value.Text.Trim(), out var v))
            {
                _log.Text = "Value must be a whole number (or left blank).";
                return;
            }
            value = v;
        }

        try
        {
            string vendor = _vendor.Text.Trim(), model = _model.Text.Trim();
            string dir = _dir.Text.Trim().Length > 0 ? _dir.Text.Trim() : DriveDossierStore.DefaultDirectory;

            _current = _current.Observe(new DriveObservation(
                DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), category.ToLowerInvariant(), detail, value, "manual"));
            DriveDossierStore.Save(dir, _current);

            _detail.Clear(); _value.Clear();
            RenderCurrent(dir, vendor, model);
            StatusBus.Report($"Drive dossier: observed [{category}] for {vendor} {model}");
            AppLog.Write($"drive-dossier {vendor} {model} --observe {category} {detail}" + (value is { } v ? $" --value {v}" : ""));
        }
        catch (Exception ex)
        {
            _log.Text = "Add observation failed: " + ex.Message;
            AppLog.WriteException("drive-dossier observe", ex);
        }
    }

    private void RenderCurrent(string dir, string vendor, string model)
    {
        if (_current is null) return;
        var seed = DriveKnowledgeBase.Find(vendor, model);
        var sb = new System.Text.StringBuilder();
        sb.Append(_current.Render(seed));
        sb.AppendLine($"  file         : {DriveDossierStore.PathFor(dir, vendor, model)}");
        if (seed is not null) sb.AppendLine($"  seed         : knowledge-base entry \"{seed.DisplayName}\"");
        _log.Text = sb.ToString();
    }
}
