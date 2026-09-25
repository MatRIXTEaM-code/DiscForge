// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DiscForge.Core.Devices;
using DiscForge.Core.Media;
using DiscForge.Devices;
using DiscForge.Devices.Media;

namespace DiscForge.App.Views;

/// <summary>
/// Detect optical drives and show what each can do (capability model), plus —
/// for the selected drive — what the disc inside it actually is.
///
/// The capability model comes from GET CONFIGURATION and answers "what media
/// will this drive accept". The details panel asks the two questions that one
/// can't: what sector-level features the drive supports (MODE SENSE page 2Ah),
/// and who manufactured the blank (ATIP for CD-R/RW, physical format and ADIP
/// for DVD). Both are read-only interrogation — nothing is written.
/// </summary>
internal sealed class DrivesView : UserControl
{
    private readonly DataGridView _grid = new()
    {
        Location = new Point(12, 48), Size = new Size(712, 168),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, Font = Theme.Ui,
        BackgroundColor = Color.White, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
    };

    private readonly Button _identify = new()
    {
        Text = "Identify disc", Location = new Point(120, 12), Width = 104,
        FlatStyle = FlatStyle.System, Enabled = false,
    };

    private readonly Label _status = new()
    {
        AutoSize = true, Location = new Point(236, 16), Font = Theme.Ui, ForeColor = Color.Gray,
    };

    private readonly TextBox _details = new()
    {
        Location = new Point(12, 248), Size = new Size(712, 200),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        BackColor = Color.White, WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 8.5f),
    };

    private IReadOnlyList<DriveCapabilities> _detected = Array.Empty<DriveCapabilities>();

    public DrivesView()
    {
        // Establish a realistic size before adding anchored children (see InspectView).
        Size = new Size(736, 470);
        BackColor = Color.White;
        Padding = new Padding(12);

        var refresh = new Button
        {
            Text = "Detect drives", Location = new Point(12, 12), Width = 100,
            FlatStyle = FlatStyle.System,
        };
        refresh.Click += async (_, _) => await DetectAsync();

        foreach (var (name, header) in new[]
        {
            ("Path", "Drive"), ("Vendor", "Vendor"), ("Model", "Model"),
            ("Cd", "CD"), ("Dvd", "DVD"), ("Bd", "BD"), ("Raw", "RAW DAO"), ("Summary", "Summary"),
        })
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header });

        _grid.SelectionChanged += (_, _) => _identify.Enabled = SelectedDrive() is not null;
        _identify.Click += async (_, _) => await IdentifyAsync();

        Controls.Add(refresh);
        Controls.Add(_identify);
        Controls.Add(_status);
        Controls.Add(new Label
        {
            Text = "Drive and disc detail:", AutoSize = true, Location = new Point(12, 228),
            Font = Theme.UiBold, ForeColor = Theme.Accent,
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
        });
        Controls.Add(_grid);
        Controls.Add(_details);

        _details.Text = "Detect drives, then select one and press \"Identify disc\".";
    }

    private async Task DetectAsync()
    {
        _grid.Rows.Clear();
        _identify.Enabled = false;
        _status.Text = "Detecting…";
        try
        {
            _detected = await Task.Run(() => DriveDetector.DetectAll());
            if (_detected.Count == 0)
            {
                _status.Text = "No optical drives detected (or raw access denied — try running as administrator).";
                return;
            }
            foreach (var d in _detected)
            {
                _grid.Rows.Add(d.DevicePath, d.Vendor, d.Model,
                    Rw(d.CdRead, d.CdWrite), Rw(d.DvdRead, d.DvdWrite), Rw(d.BdRead, d.BdWrite),
                    d.RawDao96 ? "yes" : "—", d.Summary());
                AppLog.Write($"Drive {d.DevicePath}: '{d.Vendor}' '{d.Model}' fw '{d.FirmwareRevision}' " +
                             $"CD r/w={d.CdRead}/{d.CdWrite} DVD r/w={d.DvdRead}/{d.DvdWrite} " +
                             $"BD r/w={d.BdRead}/{d.BdWrite} rawDAO96={d.RawDao96} " +
                             $"media={d.MediaProfile} " +
                             $"disc={(d.Disc is null ? "unknown" : $"{d.Disc.Status}, erasable={d.Disc.Erasable}")}");
            }
            _status.Text = $"{_detected.Count} drive(s).";
            if (_grid.Rows.Count > 0) _grid.Rows[0].Selected = true;
            StatusBus.Report($"{_detected.Count} optical drive(s) detected");
        }
        catch (Exception ex)
        {
            _status.Text = "Detection failed: " + ex.Message +
                " (raw drive access usually needs administrator).";
            AppLog.WriteException("drive detection", ex);
        }
    }

    private DriveCapabilities? SelectedDrive()
    {
        if (_grid.SelectedRows.Count == 0) return null;
        int i = _grid.SelectedRows[0].Index;
        return i >= 0 && i < _detected.Count ? _detected[i] : null;
    }

    private static char? LetterOf(DriveCapabilities drive)
    {
        var path = drive.DevicePath;            // e.g. \\.\D:
        int i = path.LastIndexOf(':');
        return i > 0 ? path[i - 1] : null;
    }

    private async Task IdentifyAsync()
    {
        var drive = SelectedDrive();
        if (drive is null) return;
        var letter = LetterOf(drive);
        if (letter is null)
        {
            _details.Text = $"No drive letter in '{drive.DevicePath}'.";
            return;
        }

        _identify.Enabled = false;
        _status.Text = "Interrogating drive…";
        _details.Text = "Reading…";
        try
        {
            var report = await Task.Run(() => MediaInfoReader.Read(letter.Value));
            _details.Text = Render(drive, report);
            _status.Text = $"{_detected.Count} drive(s).";

            AppLog.Write($"  identify {drive.DevicePath}: " +
                         $"atip={report.Identity?.AtipCode ?? "—"} " +
                         $"mediaId={report.Identity?.MediaId ?? "—"} " +
                         $"maker={report.Identity?.Manufacturer ?? "—"} " +
                         $"c2={report.Capabilities?.C2Pointers.ToString() ?? "unknown"} " +
                         $"accurateStream={report.Capabilities?.CddaAccurateStream.ToString() ?? "unknown"}");
            foreach (var n in report.Notes) AppLog.Write("    note: " + n);
        }
        catch (Exception ex)
        {
            _details.Text = "Identification failed: " + ex.Message;
            AppLog.WriteException("identify media", ex);
        }
        finally { _identify.Enabled = SelectedDrive() is not null; }
    }

    /// <summary>
    /// Lay the report out as fixed-width text. A grid would need three different
    /// shapes for CD, DVD and "drive said no", and the interesting part is mostly
    /// prose anyway — a note explaining why something is absent carries more than
    /// an empty cell would.
    /// </summary>
    private static string Render(DriveCapabilities drive, MediaInfoReport report)
    {
        var sb = new StringBuilder();
        string nl = Environment.NewLine;

        sb.Append("DRIVE").Append(nl);
        sb.Append(Row("Device", drive.DevicePath)).Append(nl);
        sb.Append(Row("Identity", $"{drive.Vendor} {drive.Model} (firmware {drive.FirmwareRevision})")).Append(nl);
        sb.Append(Row("Media loaded", drive.MediaProfile.ToString())).Append(nl);
        if (drive.Disc is not null)
            sb.Append(Row("Disc state", drive.Disc.Describe())).Append(nl);
        sb.Append(nl);

        if (report.Capabilities is { } c)
        {
            sb.Append("DRIVE CAPABILITIES (MODE SENSE 2Ah)").Append(nl);
            sb.Append(Row("Reads", Flags(
                ("CD-R", c.ReadsCdR), ("CD-RW", c.ReadsCdRw), ("DVD-ROM", c.ReadsDvdRom),
                ("DVD-R", c.ReadsDvdR), ("DVD-RAM", c.ReadsDvdRam)))).Append(nl);
            sb.Append(Row("Writes", Flags(
                ("CD-R", c.WritesCdR), ("CD-RW", c.WritesCdRw), ("DVD-R", c.WritesDvdR),
                ("DVD-RAM", c.WritesDvdRam), ("test write", c.TestWrite)))).Append(nl);
            sb.Append(Row("Sectors", Flags(
                ("Mode 2 Form 1", c.Mode2Form1), ("Mode 2 Form 2", c.Mode2Form2),
                ("multi-session", c.MultiSession)))).Append(nl);
            sb.Append(Row("Audio", Flags(
                ("CD-DA", c.CddaCommands), ("accurate stream", c.CddaAccurateStream),
                ("ISRC", c.ReadsIsrc), ("UPC", c.ReadsUpc)))).Append(nl);
            sb.Append(Row("Sub-channel", Flags(
                ("R-W", c.SubchannelRw), ("R-W corrected", c.SubchannelRwCorrected)))).Append(nl);
            sb.Append(Row("C2 pointers", c.C2Pointers ? "YES" : "no")).Append(nl);
            sb.Append(Row("Buffer", $"{c.BufferSizeKb:N0} KB" +
                (c.BufferUnderrunFree ? ", underrun-free writing" : ""))).Append(nl);
            if (c.MaxReadSpeedKbs > 0)
                sb.Append(Row("Read speed", $"{c.MaxReadSpeedKbs:N0} KB/s max " +
                    $"({DriveCapabilityPage.ToCdX(c.MaxReadSpeedKbs):0.0}× CD)")).Append(nl);
            if (c.MaxWriteSpeedKbs > 0)
                sb.Append(Row("Write speed", $"{c.MaxWriteSpeedKbs:N0} KB/s max " +
                    $"({DriveCapabilityPage.ToCdX(c.MaxWriteSpeedKbs):0.0}× CD)")).Append(nl);
            sb.Append(Row("Loading", c.Loading.ToString())).Append(nl);

            // The one capability that decides whether error recovery is even
            // possible on this hardware — worth stating, not just listing.
            sb.Append(nl);
            sb.Append(c.C2Pointers
                ? "  C2 error pointers are supported: on a bad read this drive can say"
                  + nl + "  WHICH bytes it could not correct, not merely that it failed."
                : "  No C2 error pointers: a failed read is opaque — the drive reports"
                  + nl + "  an error but not which bytes were wrong. Recovery beyond the"
                  + nl + "  drive's own correction is not possible on this hardware.");
            sb.Append(nl).Append(nl);
        }
        else
        {
            sb.Append("DRIVE CAPABILITIES").Append(nl);
            sb.Append("  Not available — see notes below.").Append(nl).Append(nl);
        }

        if (report.Identity is { } id)
        {
            sb.Append("DISC IDENTITY").Append(nl);
            if (id.AtipCode is not null)
                sb.Append(Row("ATIP code", id.AtipCode)).Append(nl);
            if (id.MediaId is not null)
                sb.Append(Row("Media ID", id.MediaId)).Append(nl);
            sb.Append(Row("Manufacturer", id.Manufacturer ?? "not in the reference table")).Append(nl);
            if (id.BookTypeName is not null)
                sb.Append(Row("Book type", id.BookTypeName)).Append(nl);
            if (id.Layers is not null)
                sb.Append(Row("Layers", id.Layers.ToString()!)).Append(nl);
            sb.Append(Row("Rewritable", id.IsRewritable ? "yes" : "no")).Append(nl);
            if (id.CapacityMb is { } mb)
                sb.Append(Row("Capacity", $"{mb:N0} MB")).Append(nl);
            if (id.LeadOut is { } lo)
                sb.Append(Row("Lead-out", $"{lo.Min:00}:{lo.Sec:00}.{lo.Frame:00}")).Append(nl);
            if (id.Encrypted == true)
                sb.Append(Row("Encryption", "the drive reports this disc is encrypted")).Append(nl);
            sb.Append(nl);
        }
        else
        {
            sb.Append("DISC IDENTITY").Append(nl);
            sb.Append("  None reported. Pressed (factory-stamped) discs carry no").Append(nl);
            sb.Append("  manufacturing identity a drive can read — only recordable").Append(nl);
            sb.Append("  media has ATIP or an ADIP media ID.").Append(nl).Append(nl);
        }

        if (report.Notes.Count > 0)
        {
            sb.Append("NOTES").Append(nl);
            foreach (var n in report.Notes)
                sb.Append("  • ").Append(n).Append(nl);
        }

        return sb.ToString();
    }

    private static string Row(string label, string value) =>
        "  " + label.PadRight(16) + value;

    private static string Flags(params (string Name, bool On)[] items)
    {
        var on = items.Where(i => i.On).Select(i => i.Name).ToList();
        return on.Count == 0 ? "—" : string.Join(", ", on);
    }

    private static string Rw(bool read, bool write) =>
        write ? "R/W" : read ? "R" : "—";
}