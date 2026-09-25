// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.PlayStation;

namespace DiscForge.App.Views;

/// <summary>
/// Build a raw Mode 2/2352 bin/cue from a folder — the psxbuild job. A tree of
/// files becomes a CD-XA data image DiscForge can browse straight back, and that
/// emulators and burners accept. It builds a faithful data image (single Mode 2
/// data track from LBA 0), not a signed/bootable one.
/// </summary>
internal sealed class PsxBuildView : UserControl
{
    private readonly TextBox _volume = new()
    {
        Text = "PSX", Width = 200, Font = Theme.Ui, Location = new Point(90, 46),
    };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Theme.Mono,
        Location = new Point(12, 96), Size = new Size(712, 344),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        BackColor = Color.White,
    };

    public PsxBuildView()
    {
        Size = new Size(736, 452);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label
        {
            Text = "Build a raw Mode 2/2352 bin/cue (CD-XA data image) from a folder of files.",
            AutoSize = true, Location = new Point(12, 14), Font = Theme.Ui,
        });
        Controls.Add(new Label { Text = "Volume ID:", AutoSize = true, Location = new Point(12, 49), Font = Theme.Ui });

        var build = new Button { Text = "Choose folder & build…", Location = new Point(320, 44), Width = 180, FlatStyle = FlatStyle.System };
        build.Click += (_, _) => Build();

        Controls.Add(_volume); Controls.Add(build); Controls.Add(_log);
        Log("Pick a folder; DiscForge lays down sync/header/subheader/EDC/ECC around an ISO 9660 tree.");
    }

    private void Build()
    {
        using var folder = new FolderBrowserDialog { Description = "Choose the folder to build into a PSX data image." };
        if (folder.ShowDialog() != DialogResult.OK) return;

        using var save = new SaveFileDialog
        {
            Filter = "Bin image (*.bin)|*.bin",
            FileName = "game.bin",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (save.ShowDialog() != DialogResult.OK) return;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(save.FileName);

        string outCue = Path.ChangeExtension(save.FileName, ".cue");
        string vol = string.IsNullOrWhiteSpace(_volume.Text) ? "PSX" : _volume.Text.Trim();
        try
        {
            int sectors = PsxImageBuilder.BuildFromFolder(folder.SelectedPath, vol, save.FileName, outCue);
            Log($"Built {Path.GetFileName(save.FileName)} — {sectors:N0} sectors " +
                $"({(long)sectors * 2352:N0} bytes) + {Path.GetFileName(outCue)}.");
            Log("Tip: open it in Browse Files to confirm the tree, or in an emulator that reads bin/cue.");
            StatusBus.Report($"Built {Path.GetFileName(save.FileName)}");
        }
        catch (Exception ex) { Log("Error: " + ex.Message); AppLog.WriteException("psx build", ex); }
    }

    private void Log(string line) => _log.AppendText(line + Environment.NewLine);
}
