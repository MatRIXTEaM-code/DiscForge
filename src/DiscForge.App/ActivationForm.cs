// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;

namespace DiscForge.App;

/// <summary>
/// The activation dialog: shows this machine's id (which the customer sends to the
/// vendor for a machine-locked key) and takes a licence key to activate. A thin shell
/// over <see cref="LicenseGate"/>.
/// </summary>
internal sealed class ActivationForm : Form
{
    private readonly TextBox _machine = new()
    {
        ReadOnly = true, Location = new Point(96, 96), Width = 300, Font = RetroTheme.Mono,
    };
    private readonly TextBox _key = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Font = RetroTheme.Mono,
        Location = new Point(16, 150), Size = new Size(452, 84),
    };
    private readonly Label _status = new()
    {
        Location = new Point(16, 244), Size = new Size(452, 20), Font = RetroTheme.Ui, ForeColor = RetroTheme.Text,
    };

    public ActivationForm()
    {
        Text = "DiscForge — Activation";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(484, 316);
        Font = RetroTheme.Ui;
        BackColor = RetroTheme.Face;
        if (RetroTheme.AppIcon is { } ic) Icon = ic;

        var st = LicenseGate.Status;
        _machine.Text = LicenseGate.MachineId;

        Controls.Add(new Label
        {
            Location = new Point(16, 16), Size = new Size(452, 60), Font = RetroTheme.Ui, ForeColor = RetroTheme.Text,
            Text = st.IsValid
                ? $"This copy is licensed to {st.Info?.Name}."
                : "This copy is unlicensed (evaluation). Paste a licence key below to activate, " +
                  "or send the machine id to your vendor for a machine-locked key.",
        });
        Controls.Add(new Label { Text = "Machine id:", AutoSize = true, Location = new Point(16, 99), Font = RetroTheme.Ui });
        Controls.Add(new Label { Text = "Licence key:", AutoSize = true, Location = new Point(16, 132), Font = RetroTheme.Ui });

        var activate = new Button
        {
            Text = "Activate", Location = new Point(16, 272), Size = new Size(96, 28),
            FlatStyle = FlatStyle.System, Font = RetroTheme.Ui,
        };
        activate.Click += (_, _) => DoActivate();

        var copyId = new Button
        {
            Text = "Copy id", Location = new Point(120, 272), Size = new Size(84, 28),
            FlatStyle = FlatStyle.System, Font = RetroTheme.Ui,
        };
        copyId.Click += (_, _) => { try { Clipboard.SetText(LicenseGate.MachineId); StatusBus.Report("Machine id copied."); } catch { } };

        var close = new Button
        {
            Text = "Close", DialogResult = DialogResult.Cancel, Location = new Point(384, 272), Size = new Size(84, 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right, FlatStyle = FlatStyle.System, Font = RetroTheme.Ui,
        };

        Controls.Add(_machine);
        Controls.Add(_key);
        Controls.Add(_status);
        Controls.Add(activate);
        Controls.Add(copyId);
        Controls.Add(close);

        AcceptButton = activate;
        CancelButton = close;

        if (st.IsValid) { _status.Text = st.Message; _status.ForeColor = Color.FromArgb(0x1C, 0x7C, 0x34); }
    }

    private void DoActivate()
    {
        var key = _key.Text.Trim();
        if (key.Length == 0) { _status.Text = "Paste a licence key first."; _status.ForeColor = RetroTheme.Text; return; }

        var r = LicenseGate.Activate(key);
        _status.Text = r.Message;
        _status.ForeColor = r.IsValid ? Color.FromArgb(0x1C, 0x7C, 0x34) : Color.FromArgb(0xB0, 0x20, 0x20);
        if (r.IsValid)
        {
            RetroMessageBox.Show(this, $"Thank you — DiscForge is now activated.\r\n\r\n{r.Message}",
                "DiscForge — Activated", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
