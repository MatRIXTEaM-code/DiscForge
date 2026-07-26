// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;

namespace DiscForge.App.Views;

/// <summary>
/// The "Select a New Disc to Burn" wizard, DiscJuggler/DVD-tool style: an
/// etched group of radio buttons for disc type, a name field, and
/// Create/Cancel/Help. Cosmetic front door for the retro skin — its result
/// (the chosen type and name) is handed to the caller, which routes to the
/// real create/burn view. No engine logic lives here.
/// </summary>
internal sealed class RetroDiscTypeDialog : Form
{
    public enum DiscType { AudioCd, Mp3Cd, Mp3Dvd, DataCd, VideoDvd, DataDvd, VideoBd, DataBd }

    public DiscType SelectedType { get; private set; } = DiscType.DataCd;
    public string DiscName => _name.Text;

    private readonly TextBox _name = new() { Font = RetroTheme.Ui };
    private readonly (DiscType Type, string Label, string DefaultName)[] _options =
    {
        (DiscType.AudioCd,  "Audio CD",         "AudioCD"),
        (DiscType.Mp3Cd,    "MP3 CD",           "MP3CD"),
        (DiscType.Mp3Dvd,   "MP3 DVD",          "MP3DVD"),
        (DiscType.DataCd,   "Data CD",          "DataCD"),
        (DiscType.VideoDvd, "Video DVD",        "DVDVideo"),
        (DiscType.DataDvd,  "Data DVD",         "DataDVD"),
        (DiscType.VideoBd,  "Video Blu-ray (BD)", "BDVideo"),
        (DiscType.DataBd,   "Data Blu-ray (BD)",  "DataBD"),
    };
    private readonly List<RadioButton> _radios = new();

    public RetroDiscTypeDialog()
    {
        Text = "Select a New Disc to Burn";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        HelpButton = true;
        ClientSize = new Size(260, 300);
        BackColor = RetroTheme.Face;
        Font = RetroTheme.Ui;
        DoubleBuffered = true;

        int y = 52;
        foreach (var opt in _options)
        {
            var rb = new RadioButton
            {
                Text = opt.Label, Tag = opt, Font = RetroTheme.Ui, AutoSize = true,
                Location = new Point(24, y), BackColor = RetroTheme.Face, ForeColor = RetroTheme.Text,
                Checked = opt.Type == DiscType.DataCd,
            };
            rb.CheckedChanged += (s, _) =>
            {
                if (((RadioButton)s!).Checked)
                {
                    var o = ((DiscType Type, string Label, string DefaultName))((RadioButton)s).Tag!;
                    SelectedType = o.Type;
                    _name.Text = o.DefaultName;
                }
            };
            _radios.Add(rb);
            Controls.Add(rb);
            y += 24;
        }

        var nameLabel = new Label
        {
            Text = "Name of the Disc:", AutoSize = true, Font = RetroTheme.Ui,
            Location = new Point(16, y + 12), BackColor = RetroTheme.Face, ForeColor = RetroTheme.Text,
        };
        _name.SetBounds(128, y + 9, 112, 20);
        _name.Text = "DataCD";
        Controls.Add(nameLabel);
        Controls.Add(_name);

        var dontShow = new CheckBox
        {
            Text = "Do not show this window again", AutoSize = true, Font = RetroTheme.Ui,
            Location = new Point(16, y + 40), BackColor = RetroTheme.Face, ForeColor = RetroTheme.Text,
        };
        Controls.Add(dontShow);

        var create = new Button { Text = "Create", DialogResult = DialogResult.OK, FlatStyle = FlatStyle.System, Font = RetroTheme.Ui };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.System, Font = RetroTheme.Ui };
        var help = new Button { Text = "Help", FlatStyle = FlatStyle.System, Font = RetroTheme.Ui };
        create.SetBounds(28, y + 68, 68, 26);
        cancel.SetBounds(104, y + 68, 68, 26);
        help.SetBounds(180, y + 68, 60, 26);
        help.Click += (_, _) => RetroMessageBox.Show(this,
            "Pick the kind of disc you want to make, give it a name, then Create.\n\n" +
            "Audio CD burns as CD-DA (TAO). Data CD/DVD/BD build a filesystem image. " +
            "The name becomes the volume label.",
            "DiscForge Help", MessageBoxButtons.OK, MessageBoxIcon.Information);

        Controls.Add(create);
        Controls.Add(cancel);
        Controls.Add(help);
        AcceptButton = create;
        CancelButton = cancel;

        Load += (_, _) => ClientSize = new Size(260, y + 106);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // "What type of disc are you burning" group frame — etched, with the
        // label notched into the top edge, enclosing just the radio list.
        int top = 34;
        var r = new Rectangle(8, top, ClientSize.Width - 16, _radios.Count * 24 + 20);
        var g = e.Graphics;
        var lblSize = TextRenderer.MeasureText("What type of disc are you burning", RetroTheme.Ui);
        int ty = r.Y;
        using (var dark = new Pen(RetroTheme.Shadow))
        using (var lite = new Pen(RetroTheme.Highlight))
        {
            g.DrawLine(dark, r.Left, ty, r.Left + 6, ty);
            g.DrawLine(lite, r.Left, ty + 1, r.Left + 6, ty + 1);
            int lx = r.Left + 6 + lblSize.Width + 6;
            g.DrawLine(dark, lx, ty, r.Right, ty);
            g.DrawLine(lite, lx, ty + 1, r.Right, ty + 1);
            g.DrawLine(dark, r.Left, ty, r.Left, r.Bottom);
            g.DrawLine(lite, r.Left + 1, ty + 1, r.Left + 1, r.Bottom);
            g.DrawLine(dark, r.Right, ty, r.Right, r.Bottom);
            g.DrawLine(dark, r.Left, r.Bottom, r.Right, r.Bottom);
            g.DrawLine(lite, r.Left + 1, r.Bottom + 1, r.Right, r.Bottom + 1);
        }
        TextRenderer.DrawText(g, "What type of disc are you burning",
            RetroTheme.Ui, new Point(r.Left + 8, r.Y - lblSize.Height / 2),
            RetroTheme.Text, TextFormatFlags.NoPadding);
    }

    /// <summary>Map the chosen disc type to a navigation key + whether it's a
    /// burn (existing image) or create (author new) intent.</summary>
    public string RouteKey() => SelectedType switch
    {
        DiscType.AudioCd => "create",   // audio compilation
        _ => "create",                  // data/video authoring
    };
}
