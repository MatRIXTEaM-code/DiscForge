// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Redump;

namespace DiscForge.App.Views;

/// <summary>
/// The redump.org submission-info generator (software half): pick a dump in any format
/// DiscForge can read and it produces the per-track and whole-image CRC-32 / MD5 / SHA-1,
/// sizes, cuesheet and (when a .sub sidecar is present) a LibCrypt/sub-channel summary —
/// with the physical fields left blank for the submitter. A thin shell over
/// <see cref="SubmissionInfoGenerator"/>.
/// </summary>
internal sealed class SubmitView : UserControl
{
    private readonly TextBox _image = new() { ReadOnly = true, Width = 470, Font = Theme.Ui, Location = new Point(90, 14) };
    private readonly TextBox _text = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = Theme.Mono,
        Location = new Point(12, 52), Size = new Size(712, 352),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom, BackColor = Color.White,
    };
    private readonly Button _save = new() { Text = "Save…", Location = new Point(650, 12), Width = 74, FlatStyle = FlatStyle.System, Enabled = false };

    private string? _imagePath;
    private string? _generated;

    public SubmitView()
    {
        Size = new Size(736, 416);
        BackColor = Color.White;
        Padding = new Padding(12);
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } f) Generate(f[0]); };

        Controls.Add(new Label { Text = "Image:", AutoSize = true, Location = new Point(12, 17), Font = Theme.Ui });
        var pick = new Button { Text = "…", Location = new Point(566, 13), Width = 30, FlatStyle = FlatStyle.System };
        pick.Click += (_, _) => Choose();
        _save.Click += (_, _) => Save();

        Controls.AddRange(new Control[] { _image, pick, _save, _text });
    }

    private static bool HasFiles(DragEventArgs e) => e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 };

    private void Choose()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Disc images (*.cue;*.bin;*.chd;*.iso;*.cdi;*.nrg)|*.cue;*.bin;*.chd;*.iso;*.cdi;*.nrg|All files (*.*)|*.*",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() == DialogResult.OK) Generate(dlg.FileName);
    }

    private void Generate(string path)
    {
        _imagePath = path; _image.Text = path;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(path);
        try
        {
            var info = SubmissionInfoGenerator.Generate(path);
            _generated = info.ToRedumpText();
            _text.Text = _generated;
            _save.Enabled = true;
            StatusBus.Report($"Submission info: {info.FileName} ({info.Tracks.Count} track(s))");
        }
        catch (Exception ex)
        {
            _generated = null; _save.Enabled = false;
            _text.Text = "Error: " + ex.Message;
            AppLog.WriteException("submission-info", ex);
        }
    }

    private void Save()
    {
        if (_generated is null) return;
        using var dlg = new SaveFileDialog
        {
            Filter = "Text (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = (_imagePath is not null ? Path.GetFileNameWithoutExtension(_imagePath) : "submission") + "_submission.txt",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, _generated);
            StatusBus.Report($"Saved {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex) { RetroMessageBox.Show(ex.Message); }
    }
}
