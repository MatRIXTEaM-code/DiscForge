// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Protect;

namespace DiscForge.App.Views;

/// <summary>Make a Reed-Solomon parity file for an image while it's good; later check the image and
/// rebuild damaged sectors from it. See <see cref="ParityFile"/>.</summary>
internal sealed class ProtectImageView : ToolViewBase
{
    private readonly TextBox _image;
    private readonly ComboBox _level = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, Location = new Point(92, 44), Font = Theme.Ui };
    private readonly Label _parInfo = new() { AutoSize = false, Location = new Point(92, 72), Size = new Size(632, 18), Font = Theme.Small, ForeColor = Theme.TextMuted };

    public ProtectImageView()
    {
        _image = AddFileRow("Image:", 12, () => PickFile("Disc images (*.iso;*.bin;*.img;*.mdf;*.cdi;*.nrg)|*.iso;*.bin;*.img;*.mdf;*.cdi;*.nrg|All files (*.*)|*.*"));
        Controls.Add(new Label { Text = "Protection:", AutoSize = true, Location = new Point(12, 47), Font = Theme.Ui });
        _level.Items.AddRange(new object[]
        {
            "Low — about 3% extra",
            "Normal — about 7% extra (recommended)",
            "High — about 14% extra",
        });
        _level.SelectedIndex = 1;
        Controls.AddRange(new Control[] { _level, _parInfo });
        _image.TextChanged += (_, _) => ShowParityInfo();
        AddButton("Create parity file", 12, 98, 150, CreateAsync);
        AddButton("Check image", 170, 98, 120, VerifyAsync);
        AddButton("Repair image", 298, 98, 120, RepairAsync);
        AddOutputArea(136);
        Output.Text =
            "A parity file is a small companion to a disc image, made while the image is good. If the image is\r\n" +
            "later damaged — bit-rot on a hard drive, a copy that went wrong, or a disc that could only be rescued\r\n" +
            "partly — DiscForge rebuilds the missing sectors from it.\r\n\r\n" +
            "The image's sectors are split into interleaved groups, so a long run of damage (like a scratch) is\r\n" +
            "spread thinly across many groups. Every sector's checksum is stored too, so DiscForge knows exactly\r\n" +
            "which sectors to rebuild.\r\n\r\n" +
            "The parity file is saved next to the image with .dfpar added. Keep a copy somewhere else as well —\r\n" +
            "on another drive or in cloud storage — so a failure that takes the image can't take both.";
    }

    private string ParityPath => ParityFile.DefaultPath(_image.Text.Trim());

    private void ShowParityInfo()
    {
        if (!File.Exists(ParityPath)) { _parInfo.Text = File.Exists(_image.Text.Trim()) ? "No parity file yet." : ""; return; }
        try
        {
            var h = ParityFile.ReadHeader(ParityPath);
            _parInfo.Text = $"Parity file found: made {h.CreatedUtc.ToLocalTime():d MMM yyyy HH:mm}, {h.P} parity per {h.K} sectors ({h.Overhead * 100:0.0}%).";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { _parInfo.Text = "The parity file can't be read: " + ex.Message; }
    }

    private IProgress<ProtectProgress> Progress()
    {
        var bar = BarProgress();
        return new Progress<ProtectProgress>(p => { bar.Report(p.Fraction); Status.Text = $"{p.Stage}…"; });
    }

    private async Task CreateAsync()
    {
        if (!RequireFile(_image, "an image")) return;
        string img = _image.Text.Trim(), par = ParityPath;
        if (File.Exists(par) && RetroMessageBox.Show("A parity file already exists for this image. Replace it?\n\nOnly do this if the image is known to be good.",
                "DiscForge", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        var level = _level.SelectedIndex switch { 0 => ParityLevel.Low, 2 => ParityLevel.High, _ => ParityLevel.Normal };
        var progress = Progress();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var h = await Task.Run(() => ParityFile.Create(img, par, level, progress: progress));
        SetStatus($"Parity file created in {sw.Elapsed:mm\\:ss}.", Theme.Good);
        Output.Text = $"Wrote {Path.GetFileName(par)} — {Human(new FileInfo(par).Length)} ({h.Overhead * 100:0.0}% of the image).\r\n\r\n" +
                      $"{h.Stripes:N0} groups of {h.K} sectors, each with {h.P} parity sectors.\r\n" +
                      $"It can rebuild up to {h.P} lost sectors in every group — for example one continuous run of\r\n" +
                      $"damage up to {Human(h.Stripes * h.P * (long)h.SectorSize)}, or scattered damage anywhere.";
        ShowParityInfo();
    }

    private async Task VerifyAsync()
    {
        if (!RequireFile(_image, "an image")) return;
        if (!File.Exists(ParityPath)) { SetStatus("There is no parity file for this image — create one first.", Theme.Warn); return; }
        string img = _image.Text.Trim(), par = ParityPath;
        var progress = Progress();
        var c = await Task.Run(() => ParityFile.Verify(img, par, progress));
        SetStatus(c.Summary(), c.Intact ? Theme.Good : c.Repairable ? Theme.Warn : Theme.Bad);
        Output.Text = Describe(c) + (c.Intact ? "" : c.Repairable ? "\r\n\r\nPress Repair image to rebuild them." : "");
    }

    private async Task RepairAsync()
    {
        if (!RequireFile(_image, "an image")) return;
        if (!File.Exists(ParityPath)) { SetStatus("There is no parity file for this image.", Theme.Warn); return; }
        string img = _image.Text.Trim(), par = ParityPath;
        var progress = Progress();
        var r = await Task.Run(() => ParityFile.Repair(img, par, progress));
        if (r.Before.Intact) { SetStatus("The image is intact — nothing to repair.", Theme.Good); Output.Text = Describe(r.Before); return; }
        SetStatus(r.Complete ? $"Repaired {r.SectorsRepaired:N0} sector(s). The image is intact again."
                             : $"Repaired {r.SectorsRepaired:N0} sector(s); {r.StillDamaged:N0} couldn't be rebuilt.",
                  r.Complete ? Theme.Good : Theme.Warn);
        Output.Text = Describe(r.Before) + $"\r\n\r\nRepaired: {r.SectorsRepaired:N0} image sector(s), {r.ParityRepaired:N0} parity sector(s)." +
                      (r.Complete ? "" : $"\r\nStill damaged: {r.StillDamaged:N0} — too many losses in the same group. A rescue from the disc, or another copy, may fill them.");
    }

    private static string Describe(ParityCheck c)
    {
        var s = $"Checked {c.DataSectors:N0} sectors.\r\n{c.Summary()}";
        if (c.DamagedSectors.Count > 0)
            s += "\r\nFirst damaged sectors: " + string.Join(", ", c.DamagedSectors.Take(20).Select(x => x.ToString("N0"))) + (c.DamagedSectors.Count > 20 ? " …" : "");
        return s;
    }
}
