// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DiscForge.Core.Audio;
using DiscForge.Core.ScummVm;

namespace DiscForge.App.Views;

/// <summary>
/// ScummVM helpers, the same Core code the CLI drives. Two jobs: fingerprint a game
/// folder or file (size + MD5 of the first 5000 bytes — ScummVM's Advanced-Detector
/// signature) to identify a title, and export a disc into a ScummVM game folder
/// (data files plus each CD audio track as trackNN, optionally FLAC/OGG).
/// </summary>
internal sealed class ScummVmView : UserControl
{
    // --- detect --------------------------------------------------------------
    private readonly TextBox _detectPath = new() { ReadOnly = true, Width = 420, Font = Theme.Ui };
    private readonly Button _detectFolder = new() { Text = "Folder…", Width = 80, FlatStyle = FlatStyle.System };
    private readonly Button _detectFile = new() { Text = "File…", Width = 70, FlatStyle = FlatStyle.System };
    private readonly CheckBox _recursive = new() { Text = "Recurse subfolders", AutoSize = true, Font = Theme.Ui };
    private readonly Button _detectGo = new() { Text = "Fingerprint", Width = 100, FlatStyle = FlatStyle.System, Enabled = false };
    private readonly TextBox _detectOut = new()
    {
        Multiline = true, ReadOnly = true, Font = Theme.Mono, ScrollBars = ScrollBars.Both, WordWrap = false,
        Size = new Size(668, 92),
    };

    // --- export --------------------------------------------------------------
    private readonly TextBox _cuePath = new() { ReadOnly = true, Width = 420, Font = Theme.Ui };
    private readonly Button _cueBrowse = new() { Text = "Cue…", Width = 80, FlatStyle = FlatStyle.System };
    private readonly TextBox _outDir = new() { ReadOnly = true, Width = 420, Font = Theme.Ui };
    private readonly Button _outBrowse = new() { Text = "Folder…", Width = 80, FlatStyle = FlatStyle.System };
    private readonly ComboBox _format = new() { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Ui };
    private readonly Label _qualityLabel = new() { Text = "OGG:", AutoSize = true, Font = Theme.Ui, Location = new Point(212, 107) };
    private readonly ComboBox _oggQuality = new() { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Ui };
    private readonly Button _exportGo = new() { Text = "Export", Width = 90, FlatStyle = FlatStyle.System, Enabled = false };

    private readonly EventLogView _log = new() { Size = new Size(668, 120) };
    private bool _busy;

    public ScummVmView()
    {
        Size = new Size(736, 560);
        BackColor = Color.White;
        Padding = new Padding(12);
        AutoScroll = true;

        // ---- detect group ----------------------------------------------------
        var detect = new GroupBox
        {
            Text = "Identify (Advanced-Detector fingerprint)", Font = Theme.UiBold, ForeColor = Theme.Text,
            Location = new Point(12, 12), Size = new Size(692, 218),
        };
        var detectHint = new Label
        {
            Text = "Size + MD5 of the first 5000 bytes of each file. Match these against a game's " +
                   "ScummVM wiki entry to identify it.",
            AutoSize = true, Location = new Point(12, 20), Font = Theme.Small, ForeColor = Theme.TextMuted,
        };
        _detectPath.Location = new Point(12, 44);
        _detectFolder.Location = new Point(438, 42);
        _detectFile.Location = new Point(522, 42);
        _recursive.Location = new Point(12, 76);
        _detectGo.Location = new Point(160, 72);
        _detectOut.Location = new Point(12, 104);
        detect.Controls.Add(detectHint);
        foreach (Control c in new Control[] { _detectPath, _detectFolder, _detectFile, _recursive, _detectGo, _detectOut })
            detect.Controls.Add(c);

        _detectFolder.Click += (_, _) => PickFolder(_detectPath, "Game folder to fingerprint…");
        _detectFile.Click += (_, _) => PickFile(_detectPath, "All files (*.*)|*.*");
        _detectGo.Click += async (_, _) => await DetectAsync();

        // ---- export group ----------------------------------------------------
        var export = new GroupBox
        {
            Text = "Export to a ScummVM game folder", Font = Theme.UiBold, ForeColor = Theme.Text,
            Location = new Point(12, 240), Size = new Size(692, 150),
        };
        var exportHint = new Label
        {
            Text = "Extracts the data files and writes each CD audio track as trackNN. FLAC and OGG are " +
                   "written by DiscForge itself (no ffmpeg); ScummVM does not read WAV for CD audio.",
            AutoSize = true, Location = new Point(12, 20), Font = Theme.Small, ForeColor = Theme.TextMuted,
        };
        _cuePath.Location = new Point(12, 44);
        _cueBrowse.Location = new Point(438, 42);
        _outDir.Location = new Point(12, 74);
        _outBrowse.Location = new Point(438, 72);
        _format.Items.AddRange(new object[] { "WAV", "FLAC", "OGG" });
        _format.SelectedIndex = 1;   // FLAC — what ScummVM usually wants
        _format.Location = new Point(12, 106);
        _oggQuality.Items.AddRange(new object[] { "Standard", "High" });
        _oggQuality.SelectedIndex = 0;
        _oggQuality.Location = new Point(252, 104);
        _format.SelectedIndexChanged += (_, _) => UpdateQualityEnabled();
        UpdateQualityEnabled();
        _exportGo.Location = new Point(112, 104);
        export.Controls.Add(exportHint);
        foreach (Control c in new Control[] { _cuePath, _cueBrowse, _outDir, _outBrowse, _format, _qualityLabel, _oggQuality, _exportGo })
            export.Controls.Add(c);

        _cueBrowse.Click += (_, _) => PickFile(_cuePath, "Cue sheet (*.cue)|*.cue|All files (*.*)|*.*", UpdateExportEnabled);
        _outBrowse.Click += (_, _) => PickFolder(_outDir, "Output folder for the ScummVM game…", UpdateExportEnabled);
        _exportGo.Click += async (_, _) => await ExportAsync();

        _log.Location = new Point(24, 400);

        Controls.Add(detect);
        Controls.Add(export);
        Controls.Add(_log);
    }

    private void PickFile(TextBox target, string filter, Action? after = null)
    {
        using var dlg = new OpenFileDialog { Filter = filter, InitialDirectory = AppSettings.LastImageDirectory ?? "" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        target.Text = dlg.FileName;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(dlg.FileName);
        if (target == _detectPath) _detectGo.Enabled = true;
        after?.Invoke();
    }

    private void PickFolder(TextBox target, string description, Action? after = null)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = description, SelectedPath = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        target.Text = dlg.SelectedPath;
        AppSettings.LastImageDirectory = dlg.SelectedPath;
        if (target == _detectPath) _detectGo.Enabled = true;
        after?.Invoke();
    }

    private void UpdateExportEnabled() =>
        _exportGo.Enabled = !_busy && _cuePath.Text.Length > 0 && _outDir.Text.Length > 0;

    // The quality choice only affects OGG (FLAC is lossless; WAV is raw).
    private void UpdateQualityEnabled()
    {
        bool ogg = _format.SelectedItem?.ToString() == "OGG";
        _oggQuality.Enabled = ogg;
        _qualityLabel.ForeColor = ogg ? Theme.Text : Color.Gray;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _detectGo.Enabled = !busy && _detectPath.Text.Length > 0;
        UpdateExportEnabled();
    }

    private async Task DetectAsync()
    {
        if (_busy || _detectPath.Text.Length == 0) return;
        var path = _detectPath.Text;
        bool recursive = _recursive.Checked;
        SetBusy(true);
        try
        {
            var prints = await Task.Run(() =>
                Directory.Exists(path)
                    ? ScummVmFingerprint.ForDirectory(path, recursive)
                    : new[] { ScummVmFingerprint.ForFile(path) });

            var sb = new StringBuilder();
            if (prints.Count == 0)
                sb.AppendLine("No files found.");
            else
            {
                int w = Math.Min(48, prints.Max(p => p.Name.Length));
                foreach (var p in prints)
                    sb.Append(p.Name.PadRight(w)).Append("  ")
                      .Append(p.Size.ToString("N0").PadLeft(12)).Append("  ").AppendLine(p.Md5);
            }
            _detectOut.Text = sb.ToString();
            _log.Add($"Fingerprinted {prints.Count} file(s).", EventLogView.Level.Good);
        }
        catch (Exception ex)
        {
            _log.Add("Fingerprint failed: " + ex.Message, EventLogView.Level.Error);
            AppLog.WriteException("scummvm fingerprint", ex);
        }
        finally { SetBusy(false); }
    }

    private async Task ExportAsync()
    {
        if (_busy || _cuePath.Text.Length == 0 || _outDir.Text.Length == 0) return;
        var cue = _cuePath.Text;
        var outDir = _outDir.Text;
        var format = _format.SelectedItem?.ToString() switch
        {
            "FLAC" => ScummVmExport.AudioFormat.Flac,
            "OGG" => ScummVmExport.AudioFormat.Ogg,
            _ => ScummVmExport.AudioFormat.Wav,
        };
        var quality = _oggQuality.SelectedIndex == 1
            ? VorbisEncoder.Quality.High
            : VorbisEncoder.Quality.Standard;

        SetBusy(true);
        try
        {
            var result = await Task.Run(() => ScummVmExport.Export(cue, outDir, format, quality));
            _log.Add($"Exported to {outDir}: {result.DataFilesExtracted} data file(s), " +
                     $"{result.AudioTracks.Count} audio track(s) as {result.AudioFormatWritten.ToString().ToLowerInvariant()}.",
                EventLogView.Level.Good);
            foreach (var w in result.Warnings)
                _log.Add(w, EventLogView.Level.Warn);
        }
        catch (Exception ex)
        {
            _log.Add("Export failed: " + ex.Message, EventLogView.Level.Error);
            AppLog.WriteException("scummvm export", ex);
        }
        finally { SetBusy(false); }
    }
}
