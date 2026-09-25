// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Chd;
using DiscForge.Core.Ciso;

namespace DiscForge.App.Views;

/// <summary>
/// Work with the compressed disc images the emulation world uses: decompress a
/// CSO/ZSO back to a plain ISO, compress an ISO to CSO, and identify a CHD and its
/// track layout. Plain container work — nothing here is protection-related.
/// </summary>
internal sealed class CompressedImageView : UserControl
{
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Theme.Mono,
        Location = new Point(12, 96), Size = new Size(712, 344),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        BackColor = Color.White,
    };

    public CompressedImageView()
    {
        Size = new Size(736, 452);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label
        {
            Text = "CSO / ZSO (block-compressed ISO) and CHD (Compressed Hunks of Data).",
            AutoSize = true, Location = new Point(12, 14), Font = Theme.Ui,
        });

        var decompress = new Button { Text = "CSO/ZSO → ISO…", Location = new Point(12, 44), Width = 150, FlatStyle = FlatStyle.System };
        decompress.Click += (_, _) => Decompress();
        var compress = new Button { Text = "ISO → CSO…", Location = new Point(172, 44), Width = 120, FlatStyle = FlatStyle.System };
        compress.Click += (_, _) => Compress();
        var chd = new Button { Text = "Identify CHD…", Location = new Point(302, 44), Width = 120, FlatStyle = FlatStyle.System };
        chd.Click += (_, _) => IdentifyChd();
        var chdExtract = new Button { Text = "CHD → image…", Location = new Point(432, 44), Width = 140, FlatStyle = FlatStyle.System };
        chdExtract.Click += (_, _) => ExtractChd();

        Controls.Add(decompress); Controls.Add(compress); Controls.Add(chd); Controls.Add(chdExtract); Controls.Add(_log);
        Log("CSO/ZSO decompress and CHD identify/extract are read operations; ISO → CSO compresses.");
    }

    private void Decompress()
    {
        using var open = new OpenFileDialog { Filter = "Compressed ISO (*.cso;*.zso)|*.cso;*.zso|All files (*.*)|*.*", InitialDirectory = AppSettings.LastImageDirectory ?? "" };
        if (open.ShowDialog() != DialogResult.OK) return;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(open.FileName);

        using var save = new SaveFileDialog { Filter = "ISO image (*.iso)|*.iso", FileName = Path.GetFileNameWithoutExtension(open.FileName) + ".iso" };
        if (save.ShowDialog() != DialogResult.OK) return;

        try
        {
            using (var input = File.OpenRead(open.FileName))
            using (var output = File.Create(save.FileName))
                CisoImage.Decompress(input, output);
            Log($"Decompressed {Path.GetFileName(open.FileName)} → {Path.GetFileName(save.FileName)} ({new FileInfo(save.FileName).Length:N0} bytes).");
            StatusBus.Report($"Decompressed {Path.GetFileName(save.FileName)}");
        }
        catch (Exception ex) { Log("Error: " + ex.Message); AppLog.WriteException("ciso decompress", ex); }
    }

    private void Compress()
    {
        using var open = new OpenFileDialog { Filter = "ISO image (*.iso)|*.iso|All files (*.*)|*.*", InitialDirectory = AppSettings.LastImageDirectory ?? "" };
        if (open.ShowDialog() != DialogResult.OK) return;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(open.FileName);

        using var save = new SaveFileDialog { Filter = "CSO image (*.cso)|*.cso", FileName = Path.GetFileNameWithoutExtension(open.FileName) + ".cso" };
        if (save.ShowDialog() != DialogResult.OK) return;

        try
        {
            long size = new FileInfo(open.FileName).Length;
            using (var input = File.OpenRead(open.FileName))
            using (var output = File.Create(save.FileName))
                CisoImage.Compress(input, size, output);
            long comp = new FileInfo(save.FileName).Length;
            Log($"Compressed {Path.GetFileName(open.FileName)} → {Path.GetFileName(save.FileName)} " +
                $"({comp:N0} bytes, {100.0 * comp / Math.Max(1, size):N1}% of original).");
            StatusBus.Report($"Compressed {Path.GetFileName(save.FileName)}");
        }
        catch (Exception ex) { Log("Error: " + ex.Message); AppLog.WriteException("ciso compress", ex); }
    }

    private void IdentifyChd()
    {
        using var open = new OpenFileDialog { Filter = "CHD image (*.chd)|*.chd|All files (*.*)|*.*", InitialDirectory = AppSettings.LastImageDirectory ?? "" };
        if (open.ShowDialog() != DialogResult.OK) return;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(open.FileName);

        try
        {
            var info = ChdReader.Read(File.ReadAllBytes(open.FileName));
            Log(info.Summary);
            foreach (var t in info.Tracks)
                Log($"  track {t.Number,2}: {t.Type} {t.Frames:N0} frames");
            Log(info.IsCd ? "Use \"CHD → image\" to extract this CD CHD to BIN/CUE."
                          : "Use \"CHD → image\" to extract this hard-disk CHD to a raw image.");
        }
        catch (Exception ex) { Log("Error: " + ex.Message); AppLog.WriteException("chd identify", ex); }
    }

    private void ExtractChd()
    {
        using var open = new OpenFileDialog { Filter = "CHD image (*.chd)|*.chd|All files (*.*)|*.*", InitialDirectory = AppSettings.LastImageDirectory ?? "" };
        if (open.ShowDialog() != DialogResult.OK) return;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(open.FileName);

        byte[] chd;
        bool isCd;
        try
        {
            chd = File.ReadAllBytes(open.FileName);
            isCd = ChdReader.Read(chd).IsCd;
        }
        catch (Exception ex) { Log("Error: " + ex.Message); AppLog.WriteException("chd read", ex); return; }

        // A CD CHD extracts to BIN + CUE; a hard-disk CHD extracts to a raw image.
        using var save = new SaveFileDialog
        {
            Filter = isCd ? "Bin image (*.bin)|*.bin" : "Raw image (*.img)|*.img|All files (*.*)|*.*",
            FileName = Path.GetFileNameWithoutExtension(open.FileName) + (isCd ? ".bin" : ".img"),
        };
        if (save.ShowDialog() != DialogResult.OK) return;

        // Delta (child) images need their parent chain; gather it on demand.
        var parents = new List<byte[]>();
        while (true)
        {
            try
            {
                if (isCd)
                {
                    var r = ChdExtractor.ExtractCd(chd, parents.ToArray());
                    File.WriteAllBytes(save.FileName, r.Bin);
                    string outCue = Path.ChangeExtension(save.FileName, ".cue");
                    File.WriteAllText(outCue, r.Cue.Replace("disc.bin", Path.GetFileName(save.FileName)));
                    Log($"Extracted {r.Tracks} track(s), {r.Bin.Length:N0} bytes → {Path.GetFileName(save.FileName)} " +
                        $"+ {Path.GetFileName(outCue)}" + (r.Verified ? " (SHA-1 verified)." : "."));
                }
                else
                {
                    var raw = ChdHdExtractor.Extract(chd, parents.ToArray());
                    File.WriteAllBytes(save.FileName, raw);
                    Log($"Extracted hard-disk image, {raw.Length:N0} bytes → {Path.GetFileName(save.FileName)} (SHA-1 verified).");
                }
                StatusBus.Report($"Extracted {Path.GetFileName(save.FileName)}");
                return;
            }
            catch (ChdFormatException ex) when (ex.Message.Contains("parent", StringComparison.OrdinalIgnoreCase)
                                                && ex.Message.Contains("supply", StringComparison.OrdinalIgnoreCase))
            {
                // This CHD (or one up its chain) is a delta image — ask for the next parent.
                if (MessageBox.Show(
                        "This CHD is a delta (child) image and needs its parent CHD to extract.\n\nSelect the parent CHD?",
                        "Parent CHD needed", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                {
                    Log("Extraction cancelled — the parent CHD was not supplied.");
                    return;
                }
                using var pick = new OpenFileDialog { Filter = "CHD image (*.chd)|*.chd|All files (*.*)|*.*", InitialDirectory = AppSettings.LastImageDirectory ?? "", Title = "Select the parent CHD" };
                if (pick.ShowDialog() != DialogResult.OK) { Log("Extraction cancelled."); return; }
                try { parents.Add(File.ReadAllBytes(pick.FileName)); }
                catch (Exception rex) { Log("Error reading parent: " + rex.Message); return; }
                Log($"Added parent {Path.GetFileName(pick.FileName)}; retrying…");
            }
            catch (Exception ex) { Log("Error: " + ex.Message); AppLog.WriteException("chd extract", ex); return; }
        }
    }

    private void Log(string line) => _log.AppendText(line + Environment.NewLine);
}
