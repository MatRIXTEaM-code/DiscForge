// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Devices;
using DiscForge.Core.Preservation;
using DiscForge.Core.Rescue;
using DiscForge.Devices;
using DiscForge.Devices.Reading;

namespace DiscForge.App.Views;

/// <summary>
/// Rescue a damaged data disc the way GNU ddrescue does: good areas first in big reads, jumping away
/// from errors, then narrowing and retrying the bad areas. The progress is kept in a ddrescue-format
/// map next to the image, so a rescue can be stopped and carried on later — or finished in another
/// drive, which then only reads what is still missing. Data discs (CD-ROM, DVD, Blu-ray) only, and
/// unencrypted only: a disc that declares copy protection is refused.
/// </summary>
internal sealed class RescueView : UserControl
{
    private readonly ComboBox _drives = new() { Width = 380, DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Ui, Location = new Point(90, 13) };
    private readonly Button _refresh = new() { Text = "Refresh", Location = new Point(478, 12), Width = 80, FlatStyle = FlatStyle.System };
    private readonly TextBox _image = new() { Width = 380, Font = Theme.Ui, Location = new Point(90, 45) };
    private readonly Button _browse = new() { Text = "…", Location = new Point(478, 44), Width = 30, FlatStyle = FlatStyle.System };
    private readonly Label _mapInfo = new() { AutoSize = false, Location = new Point(90, 70), Size = new Size(634, 18), Font = Theme.Small, ForeColor = Theme.TextMuted };
    private readonly NumericUpDown _retries = new() { Minimum = 0, Maximum = 20, Value = 1, Width = 50, Location = new Point(150, 96), Font = Theme.Ui };
    private readonly CheckBox _quick = new() { Text = "Quick: skip the slow sector-by-sector phases", AutoSize = true, Location = new Point(220, 98), Font = Theme.Ui };
    private readonly CheckBox _slowDown = new() { Text = "Slow down in damaged areas", AutoSize = true, Checked = true, Location = new Point(12, 124), Font = Theme.Ui };
    private readonly CheckBox _c2 = new() { Text = "CDs: use C2 raw reads", AutoSize = true, Checked = true, Location = new Point(220, 124), Font = Theme.Ui };
    private readonly CheckBox _salvage = new() { Text = "Last resort: fill bad sectors with the drive's best guess", AutoSize = true, Location = new Point(400, 124), Font = Theme.Ui };
    private readonly NumericUpDown _timeout = new() { Minimum = 3, Maximum = 120, Value = 20, Width = 50, Location = new Point(608, 96), Font = Theme.Ui };
    private readonly Button _start = new() { Text = "Start rescue", Location = new Point(12, 154), Width = 110, Height = 28, FlatStyle = FlatStyle.System };
    private readonly Button _stop = new() { Text = "Stop", Location = new Point(130, 154), Width = 80, Height = 28, FlatStyle = FlatStyle.System, Enabled = false };
    private readonly Label _phase = new() { AutoSize = false, Location = new Point(222, 160), Size = new Size(502, 18), Font = Theme.UiBold };
    private readonly MapBar _bar = new() { Location = new Point(12, 190), Size = new Size(712, 40), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
    private readonly Label _legend = new() { AutoSize = false, Location = new Point(12, 234), Size = new Size(712, 18), Font = Theme.Small, ForeColor = Theme.TextMuted,
        Text = "■ green rescued   ■ grey not tried yet   ■ amber failed, still to narrow down   ■ red bad sectors" };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Theme.Mono, BackColor = Color.White,
        Location = new Point(12, 256), Size = new Size(712, 150),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
    };

    private IReadOnlyList<DriveCapabilities> _detected = Array.Empty<DriveCapabilities>();
    private CancellationTokenSource? _cts;

    public RescueView()
    {
        Size = new Size(736, 420);
        BackColor = Color.White;
        Controls.Add(new Label { Text = "Drive:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Save as:", AutoSize = true, Location = new Point(12, 48), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Retry passes:", AutoSize = true, Location = new Point(12, 99), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Read timeout (s):", AutoSize = true, Location = new Point(500, 99), Font = Theme.Ui });
        Controls.AddRange(new Control[] { _drives, _refresh, _image, _browse, _mapInfo, _retries, _quick, _slowDown, _c2, _salvage, _timeout, _start, _stop, _phase, _bar, _legend, _log });

        _refresh.Click += async (_, _) => await DetectAsync();
        _browse.Click += (_, _) => Browse();
        _image.TextChanged += (_, _) => ShowMapInfo();
        _start.Click += async (_, _) => await StartAsync();
        _stop.Click += (_, _) => { _cts?.Cancel(); _stop.Enabled = false; _phase.Text = "Stopping — saving the map…"; };
        _log.Text = "Rescue copies a scratched or failing data disc (CD-ROM, DVD, Blu-ray) to an image, getting the readable " +
                    "parts first and leaving the damaged areas until last, the way GNU ddrescue does.\r\n\r\n" +
                    "Progress is saved in a map next to the image (same name + .map). You can stop at any time and press " +
                    "Start again later to carry on — or move the disc to a different drive, choose the same image, and " +
                    "start again: only the parts still missing are read. The map is GNU ddrescue's format, so ddrescue " +
                    "can continue it too.\r\n\r\nUnencrypted discs only.";
        Load += async (_, _) => await DetectAsync();
    }

    private async Task DetectAsync()
    {
        _drives.Items.Clear();
        try
        {
            _detected = await Task.Run(() => DriveDetector.DetectAll());
            foreach (var d in _detected) _drives.Items.Add(d.Summary());
            if (_drives.Items.Count > 0) _drives.SelectedIndex = 0;
            else _phase.Text = "No optical drives detected (raw access needs administrator).";
        }
        catch (Exception ex)
        {
            _phase.Text = "Drive detection failed: " + ex.Message;
            AppLog.WriteException("rescue detect", ex);
        }
    }

    private char? SelectedLetter()
    {
        if (_drives.SelectedIndex < 0 || _drives.SelectedIndex >= _detected.Count) return null;
        var path = _detected[_drives.SelectedIndex].DevicePath;
        int i = path.LastIndexOf(':');
        return i > 0 ? path[i - 1] : null;
    }

    private void Browse()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "Disc image (*.iso)|*.iso|All files (*.*)|*.*", OverwritePrompt = false,
            FileName = _image.Text.Length > 0 ? Path.GetFileName(_image.Text) : "rescued.iso",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() == DialogResult.OK) _image.Text = dlg.FileName;
    }

    private string MapPath => _image.Text + ".map";

    private void ShowMapInfo()
    {
        _bar.Show(Array.Empty<RescueBlock>(), 0);
        if (_image.Text.Length == 0) { _mapInfo.Text = ""; return; }
        if (!File.Exists(MapPath)) { _mapInfo.Text = $"New rescue. Progress will be kept in {Path.GetFileName(MapPath)}."; return; }
        try
        {
            var m = RescueMap.Load(MapPath);
            _bar.Show(m.Blocks.ToArray(), m.Size);
            _mapInfo.Text = m.IsComplete
                ? "This image is already complete."
                : $"Continues an earlier rescue: {Human(m.Rescued)} of {Human(m.Size)} rescued ({100.0 * m.Rescued / m.Size:0.00}%), {m.BadAreas} bad area(s).";
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            _mapInfo.Text = $"{Path.GetFileName(MapPath)} can't be read: {ex.Message}";
        }
    }

    private async Task StartAsync()
    {
        var letter = SelectedLetter();
        if (letter is null) { RetroMessageBox.Show("Choose a drive first."); return; }
        if (_image.Text.Trim().Length == 0) { Browse(); if (_image.Text.Trim().Length == 0) return; }
        string image = _image.Text.Trim(), mapPath = MapPath;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(Path.GetFullPath(image));
        var opt = new RescueOptions
        {
            RetryPasses = (int)_retries.Value,
            Trim = !_quick.Checked,
            Scrape = !_quick.Checked,
            SlowDownForDamage = _slowDown.Checked,
            SalvageUnverified = _salvage.Checked,
        };
        bool useC2 = _c2.Checked;
        uint timeout = (uint)_timeout.Value;

        _cts = new CancellationTokenSource();
        _start.Enabled = false; _stop.Enabled = true; _drives.Enabled = false; _image.Enabled = false; _browse.Enabled = false;
        _log.Text = "";
        var progress = new Progress<RescueProgress>(p =>
        {
            _phase.Text = $"{p.Phase}{(p.Careful ? " (drive slowed down)" : "")}: {p.PercentRescued:0.00}% rescued";
            _log.Text = $"Rescued      {Human(p.Rescued)} of {Human(p.TotalBytes)}\r\n" +
                        $"Not tried    {Human(p.NonTried)}\r\n" +
                        $"To narrow    {Human(p.NonTrimmed + p.NonScraped)}\r\n" +
                        $"Bad sectors  {Human(p.BadSector)} in {p.BadAreas} area(s)\r\n" +
                        $"Read errors  {p.ReadErrors:N0}\r\n" +
                        $"Slow reads   {p.SlowReads:N0} (areas after them are left for a later pass)\r\n" +
                        $"Time         {p.Elapsed:hh\\:mm\\:ss}";
            _bar.Show(p.Blocks, p.TotalBytes);
        });

        string summary;
        try
        {
            summary = await Task.Run(() =>
            {
                using var source = new DataDiscRescueSource(letter.Value, timeout, useC2);
                long size = source.SectorCount * source.SectorSize;
                RescueMap map;
                if (File.Exists(mapPath))
                {
                    map = RescueMap.Load(mapPath);
                    if (map.Size != size)
                        throw new IOException($"The map {Path.GetFileName(mapPath)} is for a {Human(map.Size)} disc, but this one is {Human(size)}. " +
                                              "Is it the same disc? Choose a new file name to start a separate rescue.");
                    if (!File.Exists(image)) throw new IOException($"The map exists but {Path.GetFileName(image)} doesn't. Put the image back, or delete the map to start over.");
                }
                else map = RescueMap.CreateNew(size);
                var initial = map.Blocks.ToArray();
                Invoke(() => _bar.Show(initial, size));

                using var fs = new FileStream(image, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                if (fs.Length < size) fs.SetLength(size);
                var started = DateTime.Now;
                var engine = new RescueEngine(source, fs, map, opt, progress,
                    (m, msg) => { fs.Flush(); m.Save(mapPath, $"DiscForge Rescue (drive {letter}:)", msg, started); }, _cts.Token);
                var r = engine.Run();

                var missing = map.UnfinishedSectors(source.SectorSize).ToList();
                string sidecar = BadSectorMap.SidecarPath(image);
                if (missing.Count > 0)
                    new BadSectorMap { Image = Path.GetFileName(image), TotalSectors = (int)source.SectorCount, UnreadableLba = missing, Note = $"From rescue map {Path.GetFileName(mapPath)}" + (r.Salvaged.Count > 0 ? $". {r.Salvaged.Count} of these hold the drive's uncorrected best guess instead of zeros (may be wrong)." : "") }.Save(sidecar);
                else if (File.Exists(sidecar)) File.Delete(sidecar);

                string c2Note = (source.C2Recovered > 0 ? $" {source.C2Recovered:N0} sector(s) were rebuilt from C2 raw reads." : "") +
                                (r.Salvaged.Count > 0 ? $" {r.Salvaged.Count:N0} bad sector(s) hold the drive's unverified best guess instead of zeros." : "");
                if (r.Cancelled) return $"Stopped with {100.0 * map.Rescued / size:0.00}% rescued. Press Start again to carry on.";
                if (map.IsComplete) return "Every sector was rescued — the image is complete." + c2Note;
                return $"Finished: {100.0 * map.Rescued / size:0.000}% rescued; {missing.Count:N0} sector(s) couldn't be read. " +
                       "To get more, clean the disc and start again with more retry passes, or try another drive with the same image." + c2Note;
            });
        }
        catch (Exception ex) when (ex is DiscReadException or RescueAbortException or IOException or UnauthorizedAccessException or FormatException)
        {
            summary = ex.Message;
            AppLog.WriteException("rescue", ex);
        }
        finally
        {
            _start.Enabled = true; _stop.Enabled = false; _drives.Enabled = true; _image.Enabled = true; _browse.Enabled = true;
            _cts.Dispose(); _cts = null;
        }
        _phase.Text = summary;
        _log.AppendText("\r\n\r\n" + summary);
        StatusBus.Report(summary);
        ShowMapInfo();
    }

    private static string Human(long b)
    {
        string[] u = { "B", "kB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1000 && i < u.Length - 1) { v /= 1000; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.##} {u[i]}";
    }

    /// <summary>The whole disc drawn left to right, coloured by rescue status.</summary>
    private sealed class MapBar : Control
    {
        private IReadOnlyList<RescueBlock> _blocks = Array.Empty<RescueBlock>();
        private long _size;

        public void Show(IReadOnlyList<RescueBlock> blocks, long size)
        {
            _blocks = blocks;
            _size = size;
            Invalidate();
        }

        public MapBar()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var rc = ClientRectangle;
            using (var bg = new SolidBrush(Theme.SurfaceAlt)) g.FillRectangle(bg, rc);
            var blocks = _blocks;
            if (_size > 0 && rc.Width > 2)
            {
                double scale = (double)rc.Width / _size;
                foreach (var b in blocks)
                {
                    int x0 = (int)(b.Pos * scale), x1 = Math.Max(x0 + 1, (int)(b.End * scale));
                    using var br = new SolidBrush(Colour(b.Status));
                    g.FillRectangle(br, rc.X + x0, rc.Y, x1 - x0, rc.Height);
                }
                // Problems are drawn again on top at least 1 px wide, so a single bad sector on a
                // 25 GB disc still shows.
                foreach (var b in blocks)
                    if (b.Status is RescueStatus.BadSector or RescueStatus.NonTrimmed or RescueStatus.NonScraped)
                    {
                        int x0 = (int)(b.Pos * scale);
                        using var br = new SolidBrush(Colour(b.Status));
                        g.FillRectangle(br, rc.X + x0, rc.Y, Math.Max(1, (int)(b.Size * scale)), rc.Height);
                    }
            }
            using var pen = new Pen(Theme.Border);
            g.DrawRectangle(pen, rc.X, rc.Y, rc.Width - 1, rc.Height - 1);
        }

        private static Color Colour(RescueStatus s) => s switch
        {
            RescueStatus.Finished => Color.FromArgb(0x4C, 0xAF, 0x6E),
            RescueStatus.NonTried => Color.FromArgb(0xD5, 0xD9, 0xE0),
            RescueStatus.NonTrimmed or RescueStatus.NonScraped => Color.FromArgb(0xE8, 0xA5, 0x3C),
            _ => Color.FromArgb(0xC6, 0x3A, 0x3A),
        };
    }
}
