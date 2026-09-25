// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Convert;
using DiscForge.Core.Raw;

namespace DiscForge.App.Views;

/// <summary>
/// CloneCD interop: read a <c>.ccd</c> control file's table of contents, or
/// write a <c>.ccd</c> descriptor from a CUE so DiscForge images interoperate
/// with the CloneCD ecosystem. Same engine as the CLI's ccd-info / to-ccd.
/// </summary>
internal sealed class InteropView : UserControl
{
    private readonly TextBox _path = new() { ReadOnly = true, Width = 440, Font = Theme.Ui };
    private readonly Button _browse = new() { Text = "Browse…", Width = 80, FlatStyle = FlatStyle.System };
    private readonly Button _readCcd = new() { Text = "Read .ccd", Width = 90, FlatStyle = FlatStyle.System, Enabled = false };
    private readonly Button _writeCcd = new() { Text = "CUE → .ccd", Width = 100, FlatStyle = FlatStyle.System, Enabled = false };
    private readonly TextBox _out = new()
    {
        Multiline = true, ReadOnly = true, Font = Theme.Mono, ScrollBars = ScrollBars.Vertical,
        Size = new Size(660, 280),
    };

    public InteropView()
    {
        Size = new Size(720, 460);
        BackColor = Color.White;

        var title = new Label { Text = "CloneCD interop", Font = Theme.UiBold, AutoSize = true, Location = new Point(16, 12) };
        var hint = new Label
        {
            Text = "Read a CloneCD .ccd table of contents, or write a .ccd from a CUE sheet. " +
                   "DiscForge reads and writes the CloneCD format.",
            Font = Theme.Ui, AutoSize = false, Size = new Size(680, 32), Location = new Point(16, 34),
        };

        _path.Location = new Point(16, 72);
        _browse.Location = new Point(464, 70);
        _readCcd.Location = new Point(552, 70);
        _writeCcd.Location = new Point(552, 100);
        _out.Location = new Point(16, 140);

        _browse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "CloneCD / CUE (*.ccd;*.cue)|*.ccd;*.cue|CloneCD (*.ccd)|*.ccd|CUE sheet (*.cue)|*.cue|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                _path.Text = dlg.FileName;
                var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
                _readCcd.Enabled = ext == ".ccd";
                _writeCcd.Enabled = ext == ".cue";
            }
        };
        _readCcd.Click += (_, _) => ReadCcd();
        _writeCcd.Click += (_, _) => WriteCcd();

        Controls.AddRange(new Control[] { title, hint, _path, _browse, _readCcd, _writeCcd, _out });
    }

    private void ReadCcd()
    {
        StatusBus.Report("Reading CloneCD .ccd…");
        try
        {
            var toc = CloneCdReader.ReadFile(_path.Text);
            var (img, sub) = CloneCdReader.SidecarsFor(_path.Text);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(toc.Summary);
            if (toc.Catalog is not null) sb.AppendLine($"Catalog (MCN): {toc.Catalog}");
            sb.AppendLine($"Data image: {Path.GetFileName(img)}" + (File.Exists(img) ? " (present)" : " (missing)"));
            sb.AppendLine($"Subchannel: {Path.GetFileName(sub)}" + (File.Exists(sub) ? " (present)" : " (none)"));
            sb.AppendLine();
            sb.AppendLine("Track  Type   Start LBA  Control");
            foreach (var t in toc.Tracks)
                sb.AppendLine($"  {t.Number,2}   {(t.IsData ? "Data " : "Audio")}  {t.StartLba,9}  0x{t.Control:X2}" +
                    (t.Isrc is not null ? $"  ISRC={t.Isrc}" : ""));
            _out.Text = sb.ToString();
            StatusBus.Report("CloneCD .ccd read.");
        }
        catch (Exception ex)
        {
            _out.Text = "Failed: " + ex.Message;
            StatusBus.Report("CloneCD read failed.");
        }
    }

    private void WriteCcd()
    {
        StatusBus.Report("Writing CloneCD .ccd…");
        try
        {
            using var dlg = new SaveFileDialog
            {
                Filter = "CloneCD control (*.ccd)|*.ccd",
                FileName = Path.GetFileNameWithoutExtension(_path.Text) + ".ccd",
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            using var layout = DiscLayout.FromCueFile(_path.Text);
            string ccd = CloneCdWriter.BuildCcd(layout);
            File.WriteAllText(dlg.FileName, ccd);

            var stem = Path.GetFileNameWithoutExtension(dlg.FileName);
            _out.Text = $"Wrote {Path.GetFileName(dlg.FileName)}\r\n\r\n" +
                        $"Expects alongside it: {stem}.img" +
                        (layout.HasVerbatimSubchannel ? $" and {stem}.sub" : "") + "\r\n\r\n" +
                        $"Generate the .img/.sub with:\r\n" +
                        $"  dforge build-raw \"{Path.GetFileName(_path.Text)}\" \"{stem}.img\"" +
                        (layout.HasVerbatimSubchannel ? " --verbatim" : "");
            StatusBus.Report("CloneCD .ccd written.");
        }
        catch (Exception ex)
        {
            _out.Text = "Failed: " + ex.Message;
            StatusBus.Report("CloneCD write failed.");
        }
    }
}
