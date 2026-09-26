// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Security.Cryptography;
using System.Windows.Forms;
using DiscForge.Core.Archive;
using DiscForge.Core.Preservation;

namespace DiscForge.App.Views;

/// <summary>
/// Make and check checksum files — .sfv (CRC-32), .md5 and .sha1, the formats scene releases,
/// ROM sets and download sites use — and check PAR2 recovery sets.
/// </summary>
internal sealed class ChecksumFilesView : ToolViewBase
{
    private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Location = new Point(204, 13), Font = Theme.Ui };
    private readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, Font = Theme.Ui, Location = new Point(12, 96), Size = new Size(712, 330),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
    };

    public ChecksumFilesView()
    {
        _kind.Items.AddRange(new object[] { "SFV (CRC-32)", "MD5", "SHA-1" });
        _kind.SelectedIndex = 0;
        AddButton("Make checksum file…", 12, 11, 184, MakeAsync);
        Controls.Add(_kind);
        AddButton("Check a checksum or PAR2 file…", 330, 11, 230, CheckAsync);
        Status.Location = new Point(12, 52);
        Controls.Add(Status);
        Bar.Location = new Point(12, 74);
        Controls.Add(Bar);
        _list.Columns.Add("Result", 90);
        _list.Columns.Add("File", 400);
        _list.Columns.Add("Checksum / detail", 200);
        Controls.Add(_list);
        SetStatus("Make an .sfv, .md5 or .sha1 file for a set of files, or check one (and PAR2 sets) against the files beside it.");
    }

    private static SidecarKind KindFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".sfv" => SidecarKind.Sfv,
        ".md5" => SidecarKind.Md5,
        _ => SidecarKind.Sha1,
    };

    private static string HashOf(SidecarKind kind, string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        if (kind == SidecarKind.Sfv)
        {
            var c = new DiscForge.Core.Util.Crc32();
            var buf = new byte[1 << 20];
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0) c.Update(buf.AsSpan(0, n));
            return c.Value.ToString("X8");
        }
        using HashAlgorithm alg = kind == SidecarKind.Md5 ? MD5.Create() : SHA1.Create();
        return System.Convert.ToHexString(alg.ComputeHash(fs));
    }

    private async Task MakeAsync()
    {
        using var open = new OpenFileDialog { Multiselect = true, Title = "Choose the files to checksum", InitialDirectory = AppSettings.LastImageDirectory ?? "" };
        if (open.ShowDialog() != DialogResult.OK || open.FileNames.Length == 0) return;
        var kind = _kind.SelectedIndex switch { 1 => SidecarKind.Md5, 2 => SidecarKind.Sha1, _ => SidecarKind.Sfv };
        string dir = Path.GetDirectoryName(open.FileNames[0])!;
        using var save = new SaveFileDialog
        {
            InitialDirectory = dir, FileName = Path.GetFileName(dir.TrimEnd('\\', '/')) + "." + HashSidecar.Extension(kind),
            Filter = $"{HashSidecar.Extension(kind).ToUpperInvariant()} file|*.{HashSidecar.Extension(kind)}",
        };
        if (save.ShowDialog() != DialogResult.OK) return;
        var files = open.FileNames;
        _list.Items.Clear();
        var bar = BarProgress();
        string outDir = Path.GetDirectoryName(save.FileName)!;
        var lines = await Task.Run(() =>
        {
            var l = new List<HashLine>();
            for (int i = 0; i < files.Length; i++)
            {
                l.Add(new HashLine(Path.GetRelativePath(outDir, files[i]).Replace('\\', '/'), HashOf(kind, files[i])));
                bar.Report((i + 1.0) / files.Length);
            }
            return l;
        });
        await File.WriteAllTextAsync(save.FileName, HashSidecar.Build(kind, lines));
        foreach (var l in lines) _list.Items.Add(new ListViewItem(new[] { "Added", l.Name, l.Hash }));
        SetStatus($"Wrote {Path.GetFileName(save.FileName)} with {lines.Count} file(s).", Theme.Good);
    }

    private async Task CheckAsync()
    {
        var path = PickFile("Checksum files (*.sfv;*.md5;*.sha1;*.par2)|*.sfv;*.md5;*.sha1;*.par2|All files (*.*)|*.*");
        if (path is null) return;
        _list.Items.Clear();
        if (Path.GetExtension(path).Equals(".par2", StringComparison.OrdinalIgnoreCase)) { await CheckPar2Async(path); return; }
        var kind = KindFor(path);
        var entries = HashSidecar.Parse(kind, await File.ReadAllTextAsync(path));
        string dir = Path.GetDirectoryName(path)!;
        var bar = BarProgress();
        var results = await Task.Run(() =>
        {
            var r = new List<(string Result, string Name, string Detail)>();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                string full = Path.Combine(dir, e.Name.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) r.Add(("Missing", e.Name, ""));
                else
                {
                    string h = HashOf(kind, full);
                    r.Add((string.Equals(h, e.Hash, StringComparison.OrdinalIgnoreCase) ? "OK" : "BAD", e.Name, h.Equals(e.Hash, StringComparison.OrdinalIgnoreCase) ? h : $"{h} (expected {e.Hash})"));
                }
                bar.Report((i + 1.0) / entries.Count);
            }
            return r;
        });
        foreach (var (result, name, detail) in results)
            _list.Items.Add(new ListViewItem(new[] { result, name, detail }) { ForeColor = result == "OK" ? Theme.Good : Theme.Bad });
        int bad = results.Count(r => r.Result != "OK");
        SetStatus(bad == 0 ? $"All {results.Count} file(s) match." : $"{bad} of {results.Count} file(s) are missing or don't match.", bad == 0 ? Theme.Good : Theme.Bad);
    }

    private async Task CheckPar2Async(string path)
    {
        SetStatus("Checking the PAR2 set…");
        var r = await Task.Run(() => Par2.Verify(path));
        foreach (var f in r.Files)
        {
            string result = f.Status switch { Par2FileStatus.Ok => "OK", Par2FileStatus.Missing => "Missing", _ => "Damaged" };
            string detail = f.Status == Par2FileStatus.Corrupt ? $"{f.DamagedSlices} of {f.SliceCount} block(s) damaged" : Human(f.Length);
            _list.Items.Add(new ListViewItem(new[] { result, f.Name, detail }) { ForeColor = f.Status == Par2FileStatus.Ok ? Theme.Good : Theme.Bad });
        }
        int damagedBlocks = r.Files.Where(f => f.Status == Par2FileStatus.Corrupt).Sum(f => f.DamagedSlices) +
                            r.Files.Where(f => f.Status == Par2FileStatus.Missing).Sum(f => f.SliceCount);
        bool allOk = r.Files.All(f => f.Status == Par2FileStatus.Ok);
        SetStatus(allOk ? $"All {r.Files.Count} file(s) in the PAR2 set are intact."
                        : $"{damagedBlocks} block(s) need repair; the set has {r.RecoverySlices} recovery block(s)" +
                          (damagedBlocks <= r.RecoverySlices ? " — enough to repair with a PAR2 tool (e.g. MultiPar)." : " — not enough to repair everything."),
                  allOk ? Theme.Good : damagedBlocks <= r.RecoverySlices ? Theme.Warn : Theme.Bad);
    }
}
