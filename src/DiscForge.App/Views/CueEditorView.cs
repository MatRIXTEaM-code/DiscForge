// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DiscForge.Core.Cdi;
using DiscForge.Core.Cue;

namespace DiscForge.App.Views;

/// <summary>
/// Open, check and repair a cuesheet.
///
/// A cuesheet is a set of claims about a BIN file: this track starts here, runs
/// this long, holds this kind of sector. Nothing in the text verifies those
/// claims, so a sheet can be perfectly well-formed and still describe a disc the
/// bytes beside it don't match — and the discovery arrives after the media is
/// spent. The checks here are the ones a text editor cannot make, because they
/// need the file: does the arithmetic reach the end of the BIN exactly, does
/// every index fall inside it, do the track types agree.
///
/// Editing is deliberately limited to metadata — titles, performers, ISRCs, the
/// catalogue number. Those are the fields people actually need to fix, and they
/// cannot break the layout. Changing offsets is a job for the tool that made the
/// image.
/// </summary>
internal sealed class CueEditorView : UserControl
{
    private readonly TextBox _path = new()
    {
        ReadOnly = true, Width = 320, Font = Theme.Ui, Location = new Point(70, 13),
    };
    private readonly Button _check = new()
    {
        Text = "Check", Location = new Point(478, 12), Width = 66, Height = 26,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Button _save = new()
    {
        Text = "Save", Location = new Point(550, 12), Width = 66, Height = 26,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Button _fromImage = new()
    {
        Text = "From image…", Location = new Point(622, 12), Width = 102, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    private readonly TextBox _album = new()
    {
        Width = 220, Font = Theme.Ui, Location = new Point(70, 48), Enabled = false,
    };
    private readonly TextBox _performer = new()
    {
        Width = 220, Font = Theme.Ui, Location = new Point(378, 48), Enabled = false,
    };
    private readonly TextBox _catalog = new()
    {
        Width = 140, Font = Theme.Ui, Location = new Point(70, 76), Enabled = false,
    };
    private readonly ListView _tracks = new()
    {
        Location = new Point(12, 108), Size = new Size(712, 150),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        Font = Theme.Ui, BackColor = Color.White, LabelEdit = false,
    };
    private readonly Label _verdict = new()
    {
        AutoSize = false, Location = new Point(12, 264), Size = new Size(712, 18),
        Font = new Font(Theme.Ui.FontFamily, 9f, FontStyle.Bold),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly TextBox _out = new()
    {
        Location = new Point(12, 286), Size = new Size(712, 162),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        BackColor = Color.White, WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 8.5f),
    };

    private string? _cuePath;
    private CueSheet? _cue;
    private bool _dirty;

    public CueEditorView()
    {
        Size = new Size(736, 470);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Sheet:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Album:", AutoSize = true, Location = new Point(12, 51), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Performer:", AutoSize = true, Location = new Point(304, 51), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Catalog:", AutoSize = true, Location = new Point(12, 79), Font = Theme.Ui });
        Controls.Add(new Label
        {
            Text = "(13-digit MCN, optional)", AutoSize = true, Location = new Point(216, 79),
            Font = Theme.Ui, ForeColor = Color.Gray,
        });

        var open = new Button
        {
            Text = "Open…", Location = new Point(396, 12), Width = 74, FlatStyle = FlatStyle.System,
        };
        open.Click += (_, _) => Open();
        _check.Click += (_, _) => CheckSheet();
        _save.Click += (_, _) => Save();
        _fromImage.Click += (_, _) => FromImage();

        foreach (var (name, w) in new[]
        {
            ("Track", 50), ("Type", 110), ("Start", 80), ("Title", 200), ("Performer", 160), ("ISRC", 100),
        })
            _tracks.Columns.Add(new ColumnHeader { Text = name, Width = w });

        _tracks.DoubleClick += (_, _) => EditSelectedTrack();

        foreach (var box in new[] { _album, _performer, _catalog })
            box.TextChanged += (_, _) => { if (_cue is not null) MarkDirty(); };

        Controls.Add(_path); Controls.Add(open);
        Controls.Add(_check); Controls.Add(_save); Controls.Add(_fromImage);
        Controls.Add(_album); Controls.Add(_performer); Controls.Add(_catalog);
        Controls.Add(_tracks); Controls.Add(_verdict); Controls.Add(_out);

        ShowProse(
            "Open a .cue to check it against the data file it describes." + Environment.NewLine +
            Environment.NewLine +
            "A cuesheet claims things about a BIN — where each track starts, how " +
            "long it runs, what kind of sectors it holds. Nothing in the text " +
            "checks those claims against the actual file, so a sheet can look " +
            "perfect and still describe a disc that doesn't exist." + Environment.NewLine +
            Environment.NewLine +
            "Check re-runs those tests — useful after an edit, or when the BIN " +
            "beside it has been replaced." + Environment.NewLine +
            Environment.NewLine +
            "Double-click a track to edit its title, performer or ISRC." + Environment.NewLine +
            "\"From image…\" writes a fresh sheet for a .cdi that has lost its own.");
    }

    /// <summary>Flowing text (help, errors): wrap to the window, whatever its size.</summary>
    private void ShowProse(string text) { _out.WordWrap = true; _out.Text = text; }

    /// <summary>Column-aligned monospace report: authored line breaks are the layout.</summary>
    private void ShowReport(string text) { _out.WordWrap = false; _out.Text = text; }

    private void MarkDirty()
    {
        _dirty = true;
        _save.Enabled = true;
        _save.Text = "Save *";
    }

    private void Open()
    {
        if (!ConfirmDiscard()) return;

        using var dlg = new OpenFileDialog
        {
            Filter = "Cue sheets (*.cue)|*.cue|All files (*.*)|*.*",
            InitialDirectory = AppSettings.LastCueDirectory ?? "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            var text = File.ReadAllText(dlg.FileName);
            _cue = CueSheet.Parse(text);
            _cuePath = dlg.FileName;
            _path.Text = dlg.FileName;
            AppSettings.LastCueDirectory = Path.GetDirectoryName(dlg.FileName);

            _album.Text = _cue.Title ?? "";
            _performer.Text = _cue.Performer ?? "";
            _catalog.Text = _cue.Catalog ?? "";
            _album.Enabled = _performer.Enabled = _catalog.Enabled = true;

            _dirty = false;
            _save.Enabled = false;
            _save.Text = "Save";
            _check.Enabled = true;

            RefreshTracks();
            CheckSheet();
        }
        catch (Exception ex)
        {
            _verdict.Text = "";
            ShowProse("Could not parse the sheet: " + ex.Message);
            AppLog.WriteException("cue open", ex);
        }
    }

    private void RefreshTracks()
    {
        _tracks.Items.Clear();
        if (_cue is null) return;

        foreach (var t in _cue.Tracks.OrderBy(t => t.Number))
        {
            var index1 = t.Indices.FirstOrDefault(x => x.Number == 1);
            var item = new ListViewItem(t.Number.ToString()) { Tag = t };
            item.SubItems.Add(CueSheet.TypeToToken(t.Type).token);
            item.SubItems.Add(index1?.Time.ToString() ?? "—");
            item.SubItems.Add(t.Title ?? "");
            item.SubItems.Add(t.Performer ?? "");
            item.SubItems.Add(t.Isrc ?? "");
            _tracks.Items.Add(item);
        }
    }

    /// <summary>
    /// Check the sheet against its data file and report.
    ///
    /// Not called Validate: that is an inherited ContainerControl method about
    /// focus and data binding, and hiding it would confuse anyone reading this
    /// later.
    /// </summary>
    private void CheckSheet()
    {
        if (_cue is null || _cuePath is null) return;

        var dir = Path.GetDirectoryName(Path.GetFullPath(_cuePath))!;
        CueValidation result;
        try
        {
            result = CueValidator.Validate(_cue, dir);
        }
        catch (Exception ex)
        {
            _verdict.Text = "Could not check the sheet.";
            _verdict.ForeColor = Color.FromArgb(0xA0, 0x20, 0x20);
            ShowProse(ex.Message);
            AppLog.WriteException("cue check", ex);
            return;
        }

        int errors = result.Issues.Count(i => i.Level == CueIssueLevel.Error);
        int warnings = result.Issues.Count(i => i.Level == CueIssueLevel.Warning);

        (_verdict.Text, _verdict.ForeColor) = (errors, warnings) switch
        {
            (0, 0) => ("This sheet checks out against its data file.",
                       Color.FromArgb(0x20, 0x70, 0x20)),
            (0, _) => ($"{warnings} warning(s) — worth reading, but the sheet will work.",
                       Color.FromArgb(0xA0, 0x60, 0x00)),
            _ => ($"{errors} error(s){(warnings > 0 ? $" and {warnings} warning(s)" : "")} — " +
                  "this sheet will not burn or convert correctly.",
                  Color.FromArgb(0xA0, 0x20, 0x20)),
        };

        var sb = new StringBuilder();
        sb.AppendLine($"{_cue.Tracks.Count} track(s)" +
                      (_cue.Title is not null ? $"  —  \"{_cue.Title}\"" : "") +
                      $"    checked {DateTime.Now:HH:mm:ss}");
        sb.AppendLine();

        foreach (var (file, size) in result.FileSizes)
            sb.AppendLine(size < 0
                ? $"  {file}  — NOT FOUND"
                : $"  {file}  {size:N0} bytes ({size / (1024.0 * 1024.0):N1} MB)");
        sb.AppendLine();

        if (result.Clean)
        {
            sb.AppendLine("Every index falls inside the data file, the track types agree, and the");
            sb.AppendLine("arithmetic reaches the end of the file.");
        }
        else
        {
            foreach (var level in new[] { CueIssueLevel.Error, CueIssueLevel.Warning, CueIssueLevel.Info })
            {
                var of = result.Issues.Where(i => i.Level == level).ToList();
                if (of.Count == 0) continue;

                sb.AppendLine(level switch
                {
                    CueIssueLevel.Error => "PROBLEMS — this sheet will not burn or convert correctly:",
                    CueIssueLevel.Warning => "Worth checking:",
                    _ => "For information:",
                });
                foreach (var i in of)
                {
                    sb.AppendLine();
                    foreach (var line in Wrap(i.ToString(), 72)) sb.AppendLine("  " + line);
                }
                sb.AppendLine();
            }
        }

        ShowReport(sb.ToString());
        StatusBus.Report(_verdict.Text);
    }

    private void EditSelectedTrack()
    {
        if (_cue is null || _tracks.SelectedItems.Count == 0) return;

        var track = (CueTrack)_tracks.SelectedItems[0].Tag!;
        using var dlg = new TrackMetadataDialog(track);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;

        // Records are immutable, so the edit rebuilds the track list with the
        // one changed entry swapped in.
        var updated = _cue.Tracks
            .Select(t => t.Number == track.Number
                ? t with { Title = dlg.TrackTitle, Performer = dlg.TrackPerformer, Isrc = dlg.TrackIsrc }
                : t)
            .ToList();

        _cue = _cue with { Tracks = updated };
        MarkDirty();
        RefreshTracks();
    }

    private void Save()
    {
        if (_cue is null || _cuePath is null) return;

        try
        {
            var updated = _cue with
            {
                Title = Blank(_album.Text),
                Performer = Blank(_performer.Text),
                Catalog = Blank(_catalog.Text),
            };

            // Keep the original alongside the new one. A cuesheet is small and
            // hand-written ones are often the only record of a disc's layout —
            // overwriting without a copy is a poor trade for the disk space.
            string backup = _cuePath + ".bak";
            if (File.Exists(_cuePath) && !File.Exists(backup))
                File.Copy(_cuePath, backup);

            File.WriteAllText(_cuePath, updated.Write());
            _cue = updated;
            _dirty = false;
            _save.Enabled = false;
            _save.Text = "Save";

            CheckSheet();
            StatusBus.Report($"Saved {Path.GetFileName(_cuePath)}" +
                             (File.Exists(backup) ? " (original kept as .bak)" : ""));
        }
        catch (Exception ex)
        {
            ShowProse("Could not save: " + ex.Message);
            AppLog.WriteException("cue save", ex);
        }
    }

    /// <summary>
    /// Write a cuesheet for an image that has none.
    ///
    /// A .cdi carries its own track table, so the sheet can be derived exactly
    /// rather than guessed — which is the difference between recovering a lost
    /// cuesheet and inventing a plausible one.
    /// </summary>
    private void FromImage()
    {
        if (!ConfirmDiscard()) return;

        using var open = new OpenFileDialog
        {
            Filter = "CDI images (*.cdi)|*.cdi",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (open.ShowDialog() != DialogResult.OK) return;

        AppSettings.LastImageDirectory = Path.GetDirectoryName(open.FileName);

        try
        {
            using var fs = File.OpenRead(open.FileName);
            var image = CdiParser.Parse(fs);

            string binName = Path.GetFileNameWithoutExtension(open.FileName) + ".bin";
            var tracks = new List<CueTrack>();
            long lba = 0;

            foreach (var t in image.AllTracks)
            {
                var type = t.Mode switch
                {
                    CdiTrackMode.Audio => CueTrackType.Audio,
                    CdiTrackMode.Mode1 => (int)t.SectorSize == 2048
                        ? CueTrackType.Mode1_2048 : CueTrackType.Mode1_2352,
                    _ => CueTrackType.Mode2_2352,
                };

                var indices = new List<CueIndex>();
                if (t.PregapSectors > 0)
                {
                    indices.Add(new CueIndex(0, Msf.FromSectors(lba)));
                    lba += t.PregapSectors;
                }
                indices.Add(new CueIndex(1, Msf.FromSectors(lba)));
                lba += t.LengthSectors;

                tracks.Add(new CueTrack
                {
                    Number = t.Number,
                    Type = type,
                    File = binName,
                    Indices = indices,
                });
            }

            _cue = new CueSheet { Tracks = tracks };
            _cuePath = Path.ChangeExtension(open.FileName, ".cue");
            _path.Text = _cuePath;

            _album.Text = _performer.Text = _catalog.Text = "";
            _album.Enabled = _performer.Enabled = _catalog.Enabled = true;
            _check.Enabled = true;

            RefreshTracks();
            MarkDirty();

            _verdict.Text = "Not yet checked — save the sheet, then press Check.";
            _verdict.ForeColor = Color.Gray;
            ShowProse(
                $"Built a sheet from {Path.GetFileName(open.FileName)}: " +
                $"{tracks.Count} track(s)." + Environment.NewLine +
                Environment.NewLine +
                $"It refers to '{binName}', which does not exist yet — convert the image with " +
                "Interop or the CLI to produce it, or edit the FILE name to match a BIN you " +
                "already have." + Environment.NewLine +
                Environment.NewLine +
                "Press Save to write the sheet, then Check to test it against the file.");
        }
        catch (Exception ex)
        {
            ShowProse("Could not read the image: " + ex.Message);
            AppLog.WriteException("cue from image", ex);
        }
    }

    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        return RetroMessageBox.Show(
            "The current sheet has unsaved changes. Discard them?",
            "DiscForge", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

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

/// <summary>
/// Edits one track's metadata. Deliberately not the layout: titles and ISRCs
/// cannot break a sheet, whereas an offset typed by hand very much can.
/// </summary>
internal sealed class TrackMetadataDialog : Form
{
    private readonly TextBox _title = new() { Width = 300, Font = Theme.Ui, Location = new Point(90, 16) };
    private readonly TextBox _performer = new() { Width = 300, Font = Theme.Ui, Location = new Point(90, 46) };
    private readonly TextBox _isrc = new() { Width = 140, Font = Theme.Ui, Location = new Point(90, 76) };

    public string? TrackTitle => Blank(_title.Text);
    public string? TrackPerformer => Blank(_performer.Text);
    public string? TrackIsrc => Blank(_isrc.Text);

    public TrackMetadataDialog(CueTrack track)
    {
        Text = $"Track {track.Number}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(410, 148);
        Font = Theme.Ui;

        _title.Text = track.Title ?? "";
        _performer.Text = track.Performer ?? "";
        _isrc.Text = track.Isrc ?? "";

        Controls.Add(new Label { Text = "Title:", AutoSize = true, Location = new Point(16, 19) });
        Controls.Add(new Label { Text = "Performer:", AutoSize = true, Location = new Point(16, 49) });
        Controls.Add(new Label { Text = "ISRC:", AutoSize = true, Location = new Point(16, 79) });
        Controls.Add(new Label
        {
            Text = "12 characters", AutoSize = true, Location = new Point(238, 79), ForeColor = Color.Gray,
        });

        var ok = new Button
        {
            Text = "OK", DialogResult = DialogResult.OK,
            Location = new Point(238, 110), Width = 80, FlatStyle = FlatStyle.System,
        };
        var cancel = new Button
        {
            Text = "Cancel", DialogResult = DialogResult.Cancel,
            Location = new Point(324, 110), Width = 80, FlatStyle = FlatStyle.System,
        };

        Controls.Add(_title); Controls.Add(_performer); Controls.Add(_isrc);
        Controls.Add(ok); Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}