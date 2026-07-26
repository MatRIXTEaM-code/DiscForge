// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DiscForge.Core.Gdi;

namespace DiscForge.App.Views;

/// <summary>
/// Identify a Dreamcast disc from its IP.BIN boot header — the descriptive metadata
/// at the front of a GD-ROM's bootable data track (title, product number, region,
/// supported peripherals, and so on). It reads a .gdi, a MIL-CD bin/cue (.cue), a
/// DiscJuggler .cdi, or a raw data track / ISO, all through the shared
/// <see cref="IpBin.Identify"/> path the CLI's <c>ipbin-info</c> uses.
///
/// Purely descriptive: reading the boot header names a disc, it does not unlock or
/// decrypt anything (a GD-ROM carries no encryption). Nothing here is
/// protection-related.
/// </summary>
internal sealed class IdentifyDreamcastView : UserControl
{
    private readonly TextBox _path = new()
    {
        ReadOnly = true, Width = 556, Font = Theme.Ui, Location = new Point(70, 13),
    };
    private readonly Label _summary = new()
    {
        AutoSize = false, Location = new Point(12, 46), Size = new Size(712, 24),
        Font = Theme.Ui, ForeColor = Color.Gray,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly TextBox _details = new()
    {
        Location = new Point(12, 78), Size = new Size(712, 380),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        BackColor = Color.White, WordWrap = false, Font = Theme.Mono,
    };

    public IdentifyDreamcastView()
    {
        Size = new Size(736, 470);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Image:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });
        var open = new Button { Text = "Open…", Location = new Point(632, 12), Width = 80, FlatStyle = FlatStyle.System };
        open.Click += (_, _) => Open();

        Controls.Add(_path); Controls.Add(open);
        Controls.Add(_summary);
        Controls.Add(_details);

        _summary.Text = "Open a Dreamcast image (.gdi, .cue, .cdi, or a raw .bin/.iso) to read its boot header.";
        _details.Text =
            "The IP.BIN boot header sits at the front of a GD-ROM's bootable data track." + Environment.NewLine +
            "It names the disc — title, product number, region, and the peripherals the" + Environment.NewLine +
            "game supports — and reading it is purely descriptive.";
    }

    private void Open()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Dreamcast images (*.gdi;*.cue;*.cdi;*.bin;*.iso)|*.gdi;*.cue;*.cdi;*.bin;*.iso|All files (*.*)|*.*",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        _path.Text = dlg.FileName;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(dlg.FileName);
        Identify(dlg.FileName);
    }

    private void Identify(string path)
    {
        try
        {
            IpBinHeader? h = IpBin.Identify(path);
            if (h is null)
            {
                _summary.Text = "No Dreamcast boot header (\"SEGA SEGAKATANA\") — not a bootable Dreamcast image, " +
                                "or this format isn't supported here.";
                _summary.ForeColor = Color.FromArgb(0xA0, 0x60, 0x00);
                _details.Text = "This image has no high-density Dreamcast data track to read a boot header from.";
                return;
            }

            _summary.Text = $"{h.Title}  ·  {h.ProductNumber} {h.Version}" +
                            (h.Regions.Count > 0 ? $"  ·  {string.Join("/", h.Regions)}" : "");
            _summary.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);

            var sb = new StringBuilder();
            void Row(string k, string v) => sb.AppendLine($"{k,-13}{v}");
            Row("Title:", h.Title);
            Row("Product:", $"{h.ProductNumber}  {h.Version}");
            Row("Maker:", h.Maker);
            Row("Maker ID:", h.MakerId);
            Row("Hardware:", h.HardwareId);
            Row("Device:", h.DeviceInfo);
            Row("Region:", (h.Regions.Count > 0 ? string.Join(", ", h.Regions) : "none") +
                           (h.RegionCode.Length > 0 ? $"  ({h.RegionCode})" : ""));
            Row("Released:", h.ReleaseDate);
            Row("Boot file:", h.BootFile);
            Row("Peripherals:", h.Peripherals);
            sb.AppendLine();
            sb.AppendLine("Supported peripherals:");
            if (h.SupportedPeripherals.Count > 0)
                foreach (var p in h.SupportedPeripherals) sb.AppendLine($"    - {p}");
            else
                sb.AppendLine("    (none decoded)");

            _details.Text = sb.ToString();
            StatusBus.Report($"Identified: {h.Title} ({h.ProductNumber}).");
        }
        catch (IpBinFormatException ex)
        {
            _summary.Text = "Could not read the boot header: " + ex.Message;
            _summary.ForeColor = Color.FromArgb(0xA0, 0x20, 0x20);
            _details.Text = ex.Message;
        }
        catch (Exception ex)
        {
            _summary.Text = "Could not read the image: " + ex.Message;
            _summary.ForeColor = Color.FromArgb(0xA0, 0x20, 0x20);
            _details.Text = ex.Message;
            AppLog.WriteException("identify dreamcast", ex);
        }
    }
}
