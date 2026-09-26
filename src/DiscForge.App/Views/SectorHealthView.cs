// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Raw;

namespace DiscForge.App.Views;

/// <summary>
/// Check every data sector of a raw CD image (2352-byte sectors: .bin, .img) against its own error
/// detection code, draw the result as a map of the disc, and — for sectors that fail — rebuild them
/// from the Reed-Solomon parity every Mode 1 / Mode 2 Form 1 sector carries. A repair is only kept if
/// the EDC then passes. The original is never changed: repairs go into a copy.
/// </summary>
internal sealed class SectorHealthView : ToolViewBase
{
    private enum Health : byte { Good, Repairable, Bad, NoEcc, Blank }

    private readonly TextBox _image;
    private readonly HealthMap _map = new() { Location = new Point(12, 112), Size = new Size(712, 150), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
    private readonly Label _legend = new()
    {
        AutoSize = false, Location = new Point(12, 266), Size = new Size(712, 18), Font = Theme.Small, ForeColor = Theme.TextMuted,
        Text = "■ green good   ■ amber damaged but repairable   ■ red damaged beyond repair   ■ blue audio / no parity   ■ grey blank",
    };

    public SectorHealthView()
    {
        _image = AddFileRow("Raw image:", 12, () => PickFile("Raw CD images (*.bin;*.img;*.raw)|*.bin;*.img;*.raw|All files (*.*)|*.*"), 90);
        AddButton("Check sectors", 12, 44, 130, ScanAsync);
        AddButton("Save repaired copy…", 150, 44, 170, RepairAsync);
        Status.Location = new Point(12, 80);
        Controls.Add(Status);
        Bar.Location = new Point(12, 98);
        Controls.Add(Bar);
        Controls.Add(_map);
        Controls.Add(_legend);
        Output.Location = new Point(12, 288);
        Output.Size = new Size(712, 138);
        Controls.Add(Output);
        SetStatus("For raw CD images with 2352-byte sectors. Every data sector carries its own checksum and repair data.");
    }

    private static bool IsSync(ReadOnlySpan<byte> s)
    {
        if (s[0] != 0 || s[11] != 0) return false;
        for (int i = 1; i <= 10; i++) if (s[i] != 0xFF) return false;
        return true;
    }

    private static Health Classify(byte[] s, bool repair)
    {
        bool allZero = !s.AsSpan().ContainsAnyExcept((byte)0);
        if (allZero) return Health.Blank;
        if (!IsSync(s)) return Health.NoEcc;   // audio
        int mode = s[15] & 3;
        bool m1 = mode == 1, m2f1 = mode == 2 && (s[18] & 0x20) == 0;
        if (!m1 && !m2f1) return Health.NoEcc;
        var (edcOk, _) = m1 ? EdcEcc.VerifyMode1(s) : EdcEcc.VerifyMode2Form1(s);
        if (edcOk) return Health.Good;
        var work = repair ? s : (byte[])s.Clone();
        var r = m1 ? EccCorrector.CorrectMode1(work, Array.Empty<int>()) : EccCorrector.CorrectMode2Form1(work, Array.Empty<int>());
        return r.Success ? Health.Repairable : Health.Bad;
    }

    private bool CheckImage(out long sectors)
    {
        sectors = 0;
        if (!RequireFile(_image, "a raw image")) return false;
        long len = new FileInfo(_image.Text.Trim()).Length;
        if (len % 2352 != 0)
        {
            SetStatus("This isn't a raw image (its size isn't a whole number of 2352-byte sectors). ISO images have no per-sector parity to check.", Theme.Warn);
            return false;
        }
        sectors = len / 2352;
        return true;
    }

    private async Task ScanAsync()
    {
        if (!CheckImage(out long sectors)) return;
        string path = _image.Text.Trim();
        var bar = BarProgress();
        SetStatus("Checking every sector…");
        var health = await Task.Run(() =>
        {
            var h = new Health[sectors];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            var s = new byte[2352];
            for (long i = 0; i < sectors; i++)
            {
                fs.ReadExactly(s);
                h[i] = Classify(s, repair: false);
                if ((i & 4095) == 0) bar.Report((double)i / sectors);
            }
            return h;
        });
        _map.Show(health.Select(x => (byte)x).ToArray());
        long good = health.LongCount(x => x == Health.Good), fix = health.LongCount(x => x == Health.Repairable),
             bad = health.LongCount(x => x == Health.Bad), noEcc = health.LongCount(x => x == Health.NoEcc), blank = health.LongCount(x => x == Health.Blank);
        Output.Text = $"Sectors            {sectors:N0}\r\n" +
                      $"Good               {good:N0}\r\n" +
                      $"Damaged, repairable{fix,12:N0}\r\n" +
                      $"Damaged, too badly {bad,12:N0}\r\n" +
                      $"Audio / no parity  {noEcc:N0}\r\n" +
                      $"Blank (all zeros)  {blank:N0}" +
                      (bad > 0 ? "\r\n\r\nFirst sectors beyond repair: " + string.Join(", ", Enumerable.Range(0, health.Length).Where(i => health[i] == Health.Bad).Take(15).Select(i => i.ToString("N0"))) : "") +
                      (blank > 0 ? "\r\n\r\nBlank data sectors often mean a dump filled unreadable sectors with zeros — try Rescue Disc or Merge Rips." : "");
        SetStatus(fix + bad == 0 ? "Every data sector checks out." : $"{fix + bad:N0} damaged sector(s): {fix:N0} can be repaired from their own parity.",
                  bad > 0 ? Theme.Bad : fix > 0 ? Theme.Warn : Theme.Good);
    }

    private async Task RepairAsync()
    {
        if (!CheckImage(out long sectors)) return;
        string path = _image.Text.Trim();
        using var dlg = new SaveFileDialog
        {
            InitialDirectory = Path.GetDirectoryName(path), FileName = Path.GetFileNameWithoutExtension(path) + ".repaired" + Path.GetExtension(path),
            Filter = "Raw image|*" + Path.GetExtension(path),
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        if (string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        { SetStatus("Choose a different name — the original is kept unchanged.", Theme.Warn); return; }
        string outPath = dlg.FileName;
        var bar = BarProgress();
        SetStatus("Repairing…");
        var (fixedCount, bad) = await Task.Run(() =>
        {
            long f = 0, b = 0;
            using var inp = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            using var outp = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            var s = new byte[2352];
            for (long i = 0; i < sectors; i++)
            {
                inp.ReadExactly(s);
                var h = Classify(s, repair: true);
                if (h == Health.Repairable) f++;
                else if (h == Health.Bad) b++;
                outp.Write(s);
                if ((i & 4095) == 0) bar.Report((double)i / sectors);
            }
            return (f, b);
        });
        SetStatus($"Saved {Path.GetFileName(outPath)}: {fixedCount:N0} sector(s) repaired" + (bad > 0 ? $", {bad:N0} beyond repair (copied as they were)." : "."),
                  bad > 0 ? Theme.Warn : Theme.Good);
    }

    /// <summary>The disc as a grid of cells, one colour per sector group (worst state wins).</summary>
    private sealed class HealthMap : Control
    {
        private byte[] _cells = Array.Empty<byte>();

        public HealthMap() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); }

        public void Show(byte[] health) { _cells = health; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var bg = new SolidBrush(Theme.SurfaceAlt)) g.FillRectangle(bg, ClientRectangle);
            if (_cells.Length > 0)
            {
                const int cell = 4;
                int cols = Math.Max(1, Width / cell), rows = Math.Max(1, Height / cell);
                long slots = (long)cols * rows;
                double per = Math.Max(1.0, (double)_cells.Length / slots);
                for (long slot = 0; slot < slots; slot++)
                {
                    long a = (long)(slot * per), z = Math.Min(_cells.Length, (long)((slot + 1) * per));
                    if (a >= _cells.Length) break;
                    byte worst = 0;
                    bool any = false;
                    for (long i = a; i < Math.Max(z, a + 1); i++)
                    {
                        byte v = _cells[i];
                        int rank = Rank(v);
                        if (!any || rank > Rank(worst)) worst = v;
                        any = true;
                    }
                    using var br = new SolidBrush(Colour(worst));
                    g.FillRectangle(br, (int)(slot % cols) * cell, (int)(slot / cols) * cell, cell - 1, cell - 1);
                }
            }
            using var pen = new Pen(Theme.Border);
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        private static int Rank(byte v) => (Health)v switch { Health.Bad => 4, Health.Repairable => 3, Health.Blank => 2, Health.NoEcc => 1, _ => 0 };

        private static Color Colour(byte v) => (Health)v switch
        {
            Health.Good => Color.FromArgb(0x4C, 0xAF, 0x6E),
            Health.Repairable => Color.FromArgb(0xE8, 0xA5, 0x3C),
            Health.Bad => Color.FromArgb(0xC6, 0x3A, 0x3A),
            Health.NoEcc => Color.FromArgb(0x6A, 0x9C, 0xE8),
            _ => Color.FromArgb(0xC8, 0xCC, 0xD4),
        };
    }
}
