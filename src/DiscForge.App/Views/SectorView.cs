// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DiscForge.Core.Files;
using DiscForge.Core.Raw;
using DiscForge.Core.Util;
using DiscForge.Devices;

namespace DiscForge.App.Views;

/// <summary>
/// The Sector Viewer — CDRWIN's beloved diagnostic, on DiscForge's unified
/// sector layer. Open any image (CDI, ISO, raw DAO, bare BIN), type an
/// address (LBA, mm:ss:ff, or +fileindex), and read the disc the way the
/// laser does: hex + ASCII with the sector's regions annotated, mode and
/// scramble state detected, EDC/ECC verdicts inline, and the Q frame decoded
/// when the image carries subcode.
/// </summary>
internal sealed class SectorView : UserControl
{
    private readonly TextBox _path = new() { ReadOnly = true, Width = 380, Font = Theme.Ui };
    private readonly TextBox _address = new() { Width = 90, Font = Theme.Mono, Text = "0" };
    private readonly NumericUpDown _count = new()
    {
        Width = 52, Minimum = 1, Maximum = 16, Value = 1, Font = Theme.Ui,
    };
    private readonly CheckBox _descramble = new()
    {
        Text = "Descramble", AutoSize = true, Checked = true, Font = Theme.Ui,
    };
    private readonly Button _go = new() { Text = "View", Width = 64, FlatStyle = FlatStyle.System, Enabled = false };
    private readonly Button _prev = new() { Text = "◀", Width = 34, FlatStyle = FlatStyle.System, Enabled = false };
    private readonly Button _next = new() { Text = "▶", Width = 34, FlatStyle = FlatStyle.System, Enabled = false };
    private readonly Label _identity = new()
    {
        AutoSize = true, Font = Theme.UiBold, Location = new Point(12, 74), ForeColor = Theme.Text,
    };
    private readonly TextBox _dump = new()
    {
        Multiline = true, ReadOnly = true, WordWrap = false,
        ScrollBars = ScrollBars.Both, Font = Theme.Mono,
        Location = new Point(12, 96), Size = new Size(712, 360),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        BackColor = Color.White,
    };

    private ISectorSource? _access;
    private long _current;      // file index of the first shown sector

    public SectorView()
    {
        // Anchor baseline first (see InspectView for the 150x150 trap).
        Size = new Size(736, 470);
        BackColor = Color.White;
        Padding = new Padding(12);
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = FirstFile(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { var p = FirstFile(e); if (p is not null) Open(p); };

        var open = new Button { Text = "Open Image…", Location = new Point(12, 12), Width = 100, FlatStyle = FlatStyle.System };
        open.Click += (_, _) => OpenFile();
        var openDrive = new Button { Text = "Open Drive…", Location = new Point(118, 12), Width = 100, FlatStyle = FlatStyle.System };
        openDrive.Click += async (_, _) => await OpenDriveAsync();
        _path.Location = new Point(226, 14);
        _path.Width = 300;

        int y = 44;
        Controls.Add(new Label { Text = "Address:", AutoSize = true, Location = new Point(12, y + 3), Font = Theme.Ui });
        _address.Location = new Point(70, y);
        Controls.Add(new Label { Text = "Count:", AutoSize = true, Location = new Point(172, y + 3), Font = Theme.Ui });
        _count.Location = new Point(216, y);
        _descramble.Location = new Point(282, y + 2);
        _go.Location = new Point(380, y - 1);
        _prev.Location = new Point(452, y - 1);
        _next.Location = new Point(488, y - 1);

        _go.Click += (_, _) => View(_address.Text);
        _address.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; View(_address.Text); } };
        _prev.Click += (_, _) => Step(-(long)_count.Value);
        _next.Click += (_, _) => Step((long)_count.Value);
        _descramble.CheckedChanged += (_, _) => { if (_access is not null) Render(); };

        Controls.Add(open);
        Controls.Add(openDrive);
        Controls.Add(_path);
        Controls.Add(_address);
        Controls.Add(_count);
        Controls.Add(_descramble);
        Controls.Add(_go);
        Controls.Add(_prev);
        Controls.Add(_next);
        Controls.Add(_identity);
        Controls.Add(_dump);

        _dump.Text = "Open an image (or drop one here), then address a sector.\r\n\r\n" +
                     "Addresses:  plain number = LBA   mm:ss:ff = absolute MSF   " +
                     "+N = file sector index\r\n" +
                     "For raw DAO images, 95:00:00 and up reaches into the lead-in.";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _access?.Dispose();
        base.Dispose(disposing);
    }

    private static string? FirstFile(DragEventArgs e)
        => e.Data?.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0 ? f[0] : null;

    private void OpenFile()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Disc images (*.cdi;*.iso;*.img;*.bin;*.raw)|*.cdi;*.iso;*.img;*.bin;*.raw|" +
                     "All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        Open(dlg.FileName);
    }

    public void Open(string path)
    {
        try
        {
            Adopt(SectorAccess.Open(path), path);
            StatusBus.Report($"Sector viewer: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Fail("Could not open the image: " + ex.Message, ex, $"sector-view open {path}");
        }
    }

    /// <summary>Point the viewer at a live disc — every read is a fresh SCSI
    /// command, and CDs come back with their Q sub-channel.</summary>
    private async Task OpenDriveAsync()
    {
        try
        {
            _dump.Text = "Detecting drives…";
            var drives = await Task.Run(() => DriveDetector.DetectAll());
            if (drives.Count == 0)
            {
                _dump.Text = "No optical drives detected (raw access usually needs administrator).";
                return;
            }

            var drive = drives.Count == 1 ? drives[0] : PickDrive(drives);
            if (drive is null) return;

            char? letter = drive.DevicePath.FirstOrDefault(char.IsLetter);
            if (letter is null or default(char))
            {
                _dump.Text = $"Couldn't work out the drive letter from {drive.DevicePath}.";
                return;
            }

            _dump.Text = $"Reading TOC from {letter}:…";
            var access = await Task.Run(() =>
                (ISectorSource)new DriveSectorAccess(letter.Value,
                    $"{drive.Vendor} {drive.Model}"));
            Adopt(access, $"{letter}: (live disc)");
            StatusBus.Report($"Sector viewer: live disc in {letter}:");
        }
        catch (Exception ex)
        {
            Fail("Could not open the drive: " + ex.Message +
                 "\r\n\r\nIs there a readable disc in it?", ex, "sector-view open drive");
        }
    }

    private static DiscForge.Core.Devices.DriveCapabilities? PickDrive(
        IReadOnlyList<DiscForge.Core.Devices.DriveCapabilities> drives)
    {
        using var dlg = new Form
        {
            Text = "Choose a drive",
            Size = new Size(420, 220),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false,
        };
        var list = new ListBox { Dock = DockStyle.Fill, Font = Theme.Ui };
        foreach (var d in drives)
            list.Items.Add($"{d.DevicePath}  {d.Vendor} {d.Model}");
        list.SelectedIndex = 0;
        var ok = new Button { Text = "Open", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK };
        list.DoubleClick += (_, _) => dlg.DialogResult = DialogResult.OK;
        dlg.Controls.Add(list);
        dlg.Controls.Add(ok);
        dlg.AcceptButton = ok;
        return dlg.ShowDialog() == DialogResult.OK && list.SelectedIndex >= 0
            ? drives[list.SelectedIndex] : null;
    }

    private void Adopt(ISectorSource access, string shownPath)
    {
        _access?.Dispose();
        _access = access;
        _path.Text = shownPath;
        _go.Enabled = _prev.Enabled = _next.Enabled = true;
        _identity.Text = _access.Description;
        View(_address.Text);
    }

    private void Fail(string message, Exception ex, string context)
    {
        _access?.Dispose();
        _access = null;
        _go.Enabled = _prev.Enabled = _next.Enabled = false;
        _dump.Text = message;
        AppLog.WriteException(context, ex);
    }

    private void View(string address)
    {
        if (_access is null) return;
        try
        {
            _current = _access.Resolve(address);
            Render();
        }
        catch (Exception ex)
        {
            _dump.Text = ex.Message;
        }
    }

    private void Step(long delta)
    {
        if (_access is null) return;
        _current = Math.Clamp(_current + delta, 0, Math.Max(0, _access.TotalSectors - 1));
        _address.Text = "+" + _current;
        Render();
    }

    private void Render()
    {
        if (_access is null) return;
        var sb = new StringBuilder();
        long count = (long)_count.Value;

        for (long s = _current; s < _current + count && s < _access.TotalSectors; s++)
        {
            SectorAccess.SectorData sec;
            try { sec = _access.Read(s); }
            catch (Exception ex) { sb.AppendLine(ex.Message); break; }

            var data = sec.Stored;
            string desc = Describe(ref data);

            sb.Append("── sector +").Append(sec.FileIndex);
            if (sec.LeadIn) sb.Append("  (lead-in)");
            else if (sec.Lba != long.MinValue) sb.Append("  LBA ").Append(sec.Lba);
            sb.Append("  MSF ").Append(sec.Msf);
            if (sec.Track is { } t)
            {
                sb.Append("  track ").Append(t.ToString("D2"));
                if (sec.Session is { } ses) sb.Append(" (session ").Append(ses).Append(')');
            }
            sb.Append("  [").Append(data.Length).Append(" bytes]");
            if (desc.Length > 0) sb.Append("  ").Append(desc);
            sb.AppendLine();

            for (int off = 0; off < data.Length; off += 16)
            {
                int n = Math.Min(16, data.Length - off);
                sb.Append("  ").Append(off.ToString("X4")).Append("  ");
                for (int i = 0; i < 16; i++)
                    sb.Append(i < n ? data[off + i].ToString("x2") + " " : "   ");
                sb.Append(' ');
                for (int i = 0; i < n; i++)
                {
                    byte b = data[off + i];
                    sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
                }
                var region = SectorRegion(off, data.Length);
                if (region.Length > 0) sb.Append(new string(' ', 17 - n)).Append("  ").Append(region);
                sb.AppendLine();
            }

            if (sec.Subcode is { } sub && sec.SubcodeForm is { } form)
            {
                var q = new byte[12];
                SubcodeFrame.ExtractQ(sub, form, q);
                bool crcOk = Crc16.ComputeInverted(q.AsSpan(0, 10)) == (ushort)((q[10] << 8) | q[11]);
                sb.Append("  Q: ").Append(string.Join(" ", q.Select(b => b.ToString("x2"))))
                  .Append("  (CRC ").Append(crcOk ? "OK" : "BAD")
                  .Append($"; ctrl/adr {q[0]:x2} TNO {q[1]:x2} IDX {q[2]:x2}")
                  .Append($" rel {q[3]:x2}:{q[4]:x2}:{q[5]:x2} abs {q[7]:x2}:{q[8]:x2}:{q[9]:x2})")
                  .AppendLine();
            }
            sb.AppendLine();
        }

        _dump.Text = sb.ToString();
        _identity.Text = $"{_access.Description} — showing +{_current}";
    }

    /// <summary>Mode/scramble detection with EDC/ECC verdicts; optionally
    /// swaps <paramref name="data"/> for the descrambled copy.</summary>
    private string Describe(ref byte[] data)
    {
        if (data.Length != 2352) return "";
        bool hasSync = data[0] == 0 && data[1] == 0xFF && data[11] == 0;
        if (!hasSync) return "audio (no sync)";

        var copy = (byte[])data.Clone();
        CdScrambler.ScrambleInPlace(copy);
        bool plainOk = data[15] == 1 && EdcEcc.VerifyMode1(data).EdcOk;
        bool scrambledOk = copy[15] == 1 && EdcEcc.VerifyMode1(copy).EdcOk;

        byte[] judged;
        string state;
        if (scrambledOk && !plainOk)
        {
            state = $"Mode {copy[15]}, scrambled";
            if (_descramble.Checked) { data = copy; state += " (shown descrambled)"; }
            judged = copy;
        }
        else if (copy[15] == 2 && data[15] != 2 && !plainOk)
        {
            state = "Mode 2, scrambled";
            if (_descramble.Checked) { data = copy; state += " (shown descrambled)"; }
            return state;
        }
        else
        {
            state = $"Mode {data[15]}, unscrambled";
            judged = data;
        }

        if (judged[15] == 1)
        {
            var (e, c) = EdcEcc.VerifyMode1(judged);
            state += $"; EDC {(e ? "OK" : "BAD")}, ECC {(c ? "OK" : "BAD")}";
        }
        return state;
    }

    private static string SectorRegion(int off, int len)
    {
        if (len != 2352) return "";
        return off switch
        {
            0x000 => "sync 000-00B, header 00C-00F",
            0x010 => "user data 010-80F",
            0x810 => "EDC 810-813, pad 814-81B, ECC P 81C-8C7",
            0x8C0 => "ECC Q 8C8-92F",
            _ => "",
        };
    }
}
