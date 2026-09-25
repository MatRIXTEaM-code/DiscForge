// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DiscForge.Core.Cue;
using DiscForge.Core.Forensics;

namespace DiscForge.App.Views;

/// <summary>
/// Fingerprints a PRESSING from a .cue (+ its bin): exact track geometry and pregap lengths, where
/// the audio actually sits inside each track (write-offset artifacts), and the cue's MCN/ISRC
/// identity — "the offline cousin of reading the ring code." With one cue it shows that pressing's
/// fingerprint; with two, it says SAME PRESSING / same title but a different pressing (naming every
/// differing trait) / different discs entirely. Mirrors <c>dforge pressing-dna</c>.
///
/// Like <see cref="DumpCertView"/> (and unlike <see cref="ProveView"/>), this is a pure offline
/// analysis over local files — no live drive, no persistent store, no image to render — chosen as
/// the third GUI-parity view for exactly that lower-risk profile. The cue/track-loading glue below
/// is a straight port of <c>LoadGenomeTracks</c>/<c>LoadPressingFingerprint</c> in Program.cs (same
/// steps, same order) since that loading logic is CLI-internal, not exposed as a public Core API —
/// only <see cref="PressingDna.Compute"/>/<see cref="PressingDna.Compare"/> and
/// <see cref="CueSheet.Parse"/> are.
/// </summary>
internal sealed class PressingDnaView : UserControl
{
    private readonly TextBox _cueA = new() { ReadOnly = true, Location = new Point(90, 14), Width = 522, Font = Theme.Ui };
    private readonly Button _pickA = new() { Text = "…", Location = new Point(618, 12), Width = 30, FlatStyle = FlatStyle.System };

    private readonly TextBox _cueB = new() { ReadOnly = true, Location = new Point(90, 46), Width = 522, Font = Theme.Ui };
    private readonly Button _pickB = new() { Text = "…", Location = new Point(618, 44), Width = 30, FlatStyle = FlatStyle.System };
    private readonly Button _clearB = new() { Text = "Clear", Location = new Point(654, 44), Width = 64, FlatStyle = FlatStyle.System, Enabled = false };

    private readonly Button _analyze = new()
    {
        Text = "Analyze", Location = new Point(12, 80), Width = 100, Height = 28, FlatStyle = FlatStyle.System, Enabled = false,
    };

    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Location = new Point(12, 118), Size = new Size(712, 334),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        Font = Theme.Mono, BackColor = Color.White,
    };

    private string? _pathA;
    private string? _pathB;

    public PressingDnaView()
    {
        Size = new Size(736, 464);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Cue A:", AutoSize = true, Location = new Point(12, 18), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Cue B:", AutoSize = true, Location = new Point(12, 50), Font = Theme.Ui });
        Controls.Add(new Label
        {
            Text = "Cue B is optional. With one cue, this shows its own pressing fingerprint. With two,\n" +
                   "it compares them: same pressing, same title but a different pressing, or different discs.",
            AutoSize = true, Location = new Point(12, 460), Font = Theme.Ui, ForeColor = Color.Gray,
        });

        _pickA.Click += (_, _) => PickCue(isA: true);
        _pickB.Click += (_, _) => PickCue(isA: false);
        _clearB.Click += (_, _) =>
        {
            _pathB = null;
            _cueB.Text = "";
            _clearB.Enabled = false;
            UpdateAnalyzeEnabled();
        };
        _analyze.Click += (_, _) => DoAnalyze();

        Controls.Add(_cueA); Controls.Add(_pickA);
        Controls.Add(_cueB); Controls.Add(_pickB); Controls.Add(_clearB);
        Controls.Add(_analyze);
        Controls.Add(_log);
    }

    private void PickCue(bool isA)
    {
        using var dlg = new OpenFileDialog { Filter = "CUE sheets (*.cue)|*.cue|All files (*.*)|*.*" };
        if (AppSettings.LastImageDirectory is { } dir) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog() != DialogResult.OK) return;

        AppSettings.LastImageDirectory = Path.GetDirectoryName(dlg.FileName);
        if (isA)
        {
            _pathA = dlg.FileName;
            _cueA.Text = dlg.FileName;
        }
        else
        {
            _pathB = dlg.FileName;
            _cueB.Text = dlg.FileName;
            _clearB.Enabled = true;
        }
        UpdateAnalyzeEnabled();
    }

    private void UpdateAnalyzeEnabled() => _analyze.Enabled = _pathA is not null;

    private void DoAnalyze()
    {
        if (_pathA is null) return;
        try
        {
            var (fa, na) = LoadPressingFingerprint(_pathA);
            var sb = new StringBuilder();

            if (_pathB is null)
            {
                sb.AppendLine(na);
                sb.AppendLine($"  content id  : {fa.ContentId}   (which title — offset-invariant)");
                sb.AppendLine($"  pressing id : {fa.PressingId}   (which pressing — every measured trait)");
                foreach (var t in fa.Traits) sb.AppendLine($"    {t.Name,-22} {t.Value}");
                AppLog.Write($"pressing-dna {Path.GetFileName(_pathA)}");
            }
            else
            {
                var (fb, nb) = LoadPressingFingerprint(_pathB);
                var m = PressingDna.Compare(fa, fb);
                sb.AppendLine($"A {na}  pressing {fa.PressingId}  content {fa.ContentId}");
                sb.AppendLine($"B {nb}  pressing {fb.PressingId}  content {fb.ContentId}");
                sb.AppendLine($"  => {m.Verdict}");
                foreach (var d in m.Differences) sb.AppendLine($"     {d}");
                AppLog.Write($"pressing-dna {Path.GetFileName(_pathA)} {Path.GetFileName(_pathB)} -> {m.Verdict}");
            }

            _log.Text = sb.ToString();
            StatusBus.Report($"Pressing DNA: analyzed {Path.GetFileName(_pathA)}" + (_pathB is null ? "" : $" vs {Path.GetFileName(_pathB)}"));
        }
        catch (Exception ex)
        {
            _log.Text = "Analyze failed: " + ex.Message;
            AppLog.WriteException("pressing-dna", ex);
        }
    }

    /// <summary>Port of ProveCmd's sibling <c>LoadPressingFingerprint</c> in Program.cs.</summary>
    private static (PressingFingerprint, string) LoadPressingFingerprint(string cuePath)
    {
        var tracks = LoadGenomeTracks(cuePath);
        var cue = CueSheet.Parse(File.ReadAllText(cuePath));
        var pregaps = new Dictionary<int, int>();
        var isrcs = new Dictionary<int, string>();
        foreach (var t in cue.Tracks)
        {
            if (t.Pregap is { } pg) pregaps[t.Number] = (int)pg.ToSectors();
            else
            {
                var i0 = t.Indices.FirstOrDefault(i => i.Number == 0);
                var i1 = t.Indices.FirstOrDefault(i => i.Number == 1);
                if (i0 is not null && i1 is not null)
                    pregaps[t.Number] = (int)(i1.Time.ToSectors() - i0.Time.ToSectors());
            }
            if (!string.IsNullOrEmpty(t.Isrc)) isrcs[t.Number] = t.Isrc;
        }
        var fp = PressingDna.Compute(tracks, pregaps, cue.Catalog, isrcs.Count > 0 ? isrcs : null);
        return (fp, Path.GetFileName(cuePath));
    }

    /// <summary>Port of <c>LoadGenomeTracks</c> in Program.cs: reads the cue and the bin(s) it
    /// references into <see cref="GenomeTrack"/>s, same rules (one file shared by every track vs.
    /// one file per track).</summary>
    private static List<GenomeTrack> LoadGenomeTracks(string cuePath)
    {
        if (!File.Exists(cuePath)) throw new FileNotFoundException($"Cue not found: {cuePath}");
        var cue = CueSheet.Parse(File.ReadAllText(cuePath));
        if (cue.Tracks.Count == 0) throw new InvalidDataException("The cue lists no tracks.");
        string dir = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? ".";

        static bool IsAudio(CueTrackType t) => t == CueTrackType.Audio;
        static long StartSector(CueTrack t)
        {
            var idx = t.Indices.FirstOrDefault(i => i.Number == 1) ?? t.Indices.FirstOrDefault();
            return idx?.Time.ToSectors() ?? 0;
        }
        string Resolve(string file)
        {
            string p = Path.Combine(dir, file);
            if (File.Exists(p)) return p;
            string alt = Path.Combine(dir, Path.GetFileNameWithoutExtension(cuePath) + Path.GetExtension(file));
            if (File.Exists(alt)) return alt;
            throw new FileNotFoundException($"Bin referenced by the cue not found: {file}");
        }

        var tracks = new List<GenomeTrack>();
        var distinctFiles = cue.Tracks.Select(t => t.File).Distinct().ToList();

        if (distinctFiles.Count == 1)
        {
            var bytes = File.ReadAllBytes(Resolve(distinctFiles[0]));
            long totalSectors = bytes.Length / 2352;
            var ordered = cue.Tracks.OrderBy(t => t.Number).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                long start = StartSector(ordered[i]);
                long end = i + 1 < ordered.Count ? StartSector(ordered[i + 1]) : totalSectors;
                start = Math.Clamp(start, 0, totalSectors);
                end = Math.Clamp(end, start, totalSectors);
                var content = bytes.AsSpan((int)(start * 2352), (int)((end - start) * 2352)).ToArray();
                tracks.Add(new(ordered[i].Number, !IsAudio(ordered[i].Type), content));
            }
        }
        else
        {
            foreach (var t in cue.Tracks.OrderBy(t => t.Number))
                tracks.Add(new(t.Number, !IsAudio(t.Type), File.ReadAllBytes(Resolve(t.File))));
        }
        return tracks;
    }
}
