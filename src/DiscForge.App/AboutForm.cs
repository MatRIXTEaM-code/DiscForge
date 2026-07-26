// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace DiscForge.App;

/// <summary>
/// About / product information.
///
/// This is often the only place a customer looks for the version and licence, so
/// it earns its keep: name, version, what it is, and how to get the diagnostics
/// out. Restrained on purpose — a paid tool shouldn't feel like shareware.
/// </summary>
internal sealed class AboutForm : Form
{
    public AboutForm()
    {
        Text = "About DiscForge";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(460, 356);
        Font = RetroTheme.Ui;
        BackColor = RetroTheme.Face;

        var banner = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = RetroTheme.TitleActive };
        banner.Paint += PaintBanner;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        string versionText = version is null ? "" : $"{version.Major}.{version.Minor}.{version.Build}";

        var body = new Label
        {
            Location = new Point(16, 88),
            Size = new Size(430, 120),
            Font = RetroTheme.Ui,
            ForeColor = RetroTheme.Text,
            Text =
                $"Version {versionText}\r\n\r\n" +
                "A clean-room disc-imaging and retro-preservation toolkit. Reads, writes, " +
                "verifies and converts optical images (CD / DVD / Blu-ray and console discs), " +
                "cartridge ROMs, floppies, saves and game audio — plus a library manager, " +
                "1G1R set builder, universal converter and front-end export.\r\n\r\n" +
                "Built from published format specifications and observed hardware behaviour. " +
                "Reads unencrypted media only.",
        };

        var copyright = new Label
        {
            Location = new Point(16, 214),
            AutoSize = true,
            Font = RetroTheme.Ui,
            ForeColor = RetroTheme.Text,
            Text = "Copyright \u00A9 2026 MaTRIX TeAm. All rights reserved.\r\n" +
                   "This software is proprietary. See the licence for terms of use.",
        };

        var status = LicenseGate.Status;
        var licenceStatus = new Label
        {
            Location = new Point(16, 258), Size = new Size(430, 20), Font = RetroTheme.UiBold,
            ForeColor = status.IsValid ? Color.FromArgb(0x1C, 0x7C, 0x34) : Color.FromArgb(0xB0, 0x5A, 0x00),
            Text = status.IsValid ? $"Licensed to {status.Info?.Name} ({status.Info?.Edition})" : "Unlicensed — evaluation copy",
        };

        var diagnostics = new Button
        {
            Text = "Open log folder", Location = new Point(16, 300), Width = 120, Height = 26,
            FlatStyle = FlatStyle.System, Font = RetroTheme.Ui,
        };
        diagnostics.Click += (_, _) => OpenLogFolder();

        var licence = new Button
        {
            Text = "Licence", Location = new Point(144, 300), Width = 84, Height = 26,
            FlatStyle = FlatStyle.System, Font = RetroTheme.Ui,
        };
        licence.Click += (_, _) => ShowLicence();

        var activate = new Button
        {
            Text = "Activate…", Location = new Point(236, 300), Width = 92, Height = 26,
            FlatStyle = FlatStyle.System, Font = RetroTheme.Ui,
        };
        activate.Click += (_, _) =>
        {
            using var a = new ActivationForm();
            a.ShowDialog(this);
            var s = LicenseGate.Status;
            licenceStatus.Text = s.IsValid ? $"Licensed to {s.Info?.Name} ({s.Info?.Edition})" : "Unlicensed — evaluation copy";
            licenceStatus.ForeColor = s.IsValid ? Color.FromArgb(0x1C, 0x7C, 0x34) : Color.FromArgb(0xB0, 0x5A, 0x00);
        };

        var ok = new Button
        {
            Text = "Close", DialogResult = DialogResult.OK,
            Size = new Size(84, 26), Location = new Point(360, 300),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            FlatStyle = FlatStyle.System, Font = RetroTheme.Ui,
        };

        Controls.Add(body);
        Controls.Add(copyright);
        Controls.Add(licenceStatus);
        Controls.Add(diagnostics);
        Controls.Add(licence);
        Controls.Add(activate);
        Controls.Add(ok);
        Controls.Add(banner);          // added last so it docks to the very top

        AcceptButton = ok;
        CancelButton = ok;
    }

    private static void PaintBanner(object? sender, PaintEventArgs e)
    {
        var c = (Control)sender!;
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using (var bg = new System.Drawing.Drawing2D.LinearGradientBrush(
                   c.ClientRectangle, RetroTheme.TitleActive, RetroTheme.TitleActive2,
                   System.Drawing.Drawing2D.LinearGradientMode.Horizontal))
            g.FillRectangle(bg, c.ClientRectangle);

        // The mark: a disc, drawn in code — no bitmap to ship or lose.
        using var ring = new Pen(Color.FromArgb(150, Color.White), 2f);
        using var inner = new Pen(Color.FromArgb(80, Color.White), 1f);
        using var hub = new SolidBrush(Color.FromArgb(90, Color.White));
        g.DrawEllipse(ring, 16, 16, 40, 40);
        g.DrawEllipse(inner, 24, 24, 24, 24);
        g.FillEllipse(hub, 30, 30, 12, 12);

        using var titleFont = new Font("MS Sans Serif", 14f, FontStyle.Bold);
        using var subFont = new Font("MS Sans Serif", 8f);
        g.DrawString("DiscForge", titleFont, Brushes.White, 68, 14);
        using var sub = new SolidBrush(Color.FromArgb(210, Color.White));
        g.DrawString("CD / DVD / Blu-ray imaging", subFont, sub, 70, 42);
    }

    private static void OpenLogFolder()
    {
        try
        {
            var dir = Path.GetDirectoryName(AppLog.FilePath)!;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            RetroMessageBox.Show("Could not open the log folder: " + ex.Message, "DiscForge");
        }
    }

    private void ShowLicence()
    {
        // Show the licence that shipped, not a copy that might drift from it.
        var path = Path.Combine(AppContext.BaseDirectory, "LICENSE");
        if (File.Exists(path))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                return;
            }
            catch { /* no handler for an extensionless file: fall through */ }
        }

        RetroMessageBox.Show(this,
            "DiscForge is proprietary software.\r\n\r\n" +
            "Copyright \u00A9 2026 MaTRIX TeAm. All rights reserved.\r\n\r\n" +
            "No permission is granted to copy, publish, distribute, sell, fork or " +
            "create derivative works, except under a separate written agreement.\r\n\r\n" +
            "The full licence accompanies your copy.",
            "DiscForge — Licence", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
