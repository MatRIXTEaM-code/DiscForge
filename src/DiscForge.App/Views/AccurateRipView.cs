// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Audio;
using DiscForge.Core.Raw;

namespace DiscForge.App.Views;

/// <summary>
/// AccurateRip verification: point it at an audio CUE and it computes each
/// track's v1/v2 checksums and the disc IDs, then — if you supply a downloaded
/// AccurateRip database record (.bin) — reports per-track ACCURATE / mismatch.
/// The same engine the CLI uses; fetching the record stays an online step.
/// </summary>
internal sealed class AccurateRipView : UserControl
{
    private readonly TextBox _cue = new() { ReadOnly = true, Width = 380, Font = Theme.Ui };
    private readonly Button _browseCue = new() { Text = "CUE…", Width = 70, FlatStyle = FlatStyle.System };
    private readonly TextBox _db = new() { ReadOnly = true, Width = 380, Font = Theme.Ui };
    private readonly Button _browseDb = new() { Text = "Record…", Width = 70, FlatStyle = FlatStyle.System };
    private readonly Button _run = new() { Text = "Compute / Verify", Width = 130, FlatStyle = FlatStyle.System, Enabled = false };
    private readonly TextBox _out = new()
    {
        Multiline = true, ReadOnly = true, Font = Theme.Mono, ScrollBars = ScrollBars.Vertical,
        Size = new Size(660, 280),
    };

    public AccurateRipView()
    {
        Size = new Size(720, 460);
        BackColor = Color.White;

        var title = new Label { Text = "AccurateRip verification", Font = Theme.UiBold, AutoSize = true, Location = new Point(16, 12) };
        var hint = new Label
        {
            Text = "Confirms an audio rip is bit-perfect against the AccurateRip database. " +
                   "Pick a CUE; optionally add a downloaded record to verify.",
            Font = Theme.Ui, AutoSize = false, Size = new Size(680, 32), Location = new Point(16, 34),
        };

        var cueLabel = new Label { Text = "Audio CUE:", AutoSize = true, Font = Theme.Ui, Location = new Point(16, 74) };
        _cue.Location = new Point(90, 71); _browseCue.Location = new Point(474, 69);
        var dbLabel = new Label { Text = "Record:", AutoSize = true, Font = Theme.Ui, Location = new Point(16, 104) };
        _db.Location = new Point(90, 101); _browseDb.Location = new Point(474, 99);
        _run.Location = new Point(552, 84);
        _out.Location = new Point(16, 140);

        _browseCue.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "CUE sheet (*.cue)|*.cue|All files (*.*)|*.*" };
            if (dlg.ShowDialog() == DialogResult.OK) { _cue.Text = dlg.FileName; _run.Enabled = true; }
        };
        _browseDb.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "AccurateRip record (*.bin)|*.bin|All files (*.*)|*.*" };
            if (dlg.ShowDialog() == DialogResult.OK) _db.Text = dlg.FileName;
        };
        _run.Click += async (_, _) => await RunAsync();

        Controls.AddRange(new Control[] { title, hint, cueLabel, _cue, _browseCue, dbLabel, _db, _browseDb, _run, _out });
    }

    private async Task RunAsync()
    {
        _run.Enabled = false;
        _out.Text = "Working…";
        StatusBus.Report("Computing AccurateRip checksums…");
        string cuePath = _cue.Text, dbPath = _db.Text;

        try
        {
            string report = await Task.Run(() => Compute(cuePath, dbPath));
            _out.Text = report;
            StatusBus.Report("AccurateRip complete.");
        }
        catch (Exception ex)
        {
            _out.Text = "Failed: " + ex.Message;
            StatusBus.Report("AccurateRip failed.");
        }
        finally { _run.Enabled = true; }
    }

    private static string Compute(string cuePath, string dbPath)
    {
        using var layout = DiscLayout.FromCueFile(cuePath);
        var audio = layout.Tracks.Where(t => t.Mode == RawTrackMode.Audio).OrderBy(t => t.Number).ToList();
        if (audio.Count == 0) return "No audio tracks — AccurateRip applies to audio CDs.";

        var offsets = new List<int>();
        int lba = 0;
        foreach (var t in layout.Tracks.OrderBy(t => t.Number)) { offsets.Add(lba); lba += t.TotalSectors; }
        offsets.Add(lba);

        var (id1, id2, cddb) = AccurateRip.DiscIds(offsets);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{audio.Count} audio track(s)");
        sb.AppendLine($"Disc IDs:  AR1={id1:X8}  AR2={id2:X8}  CDDB={cddb:X8}");
        sb.AppendLine($"Lookup:    {AccurateRipDatabase.LookupUrl(audio.Count, id1, id2, cddb)}");
        sb.AppendLine();
        sb.AppendLine("Track  AccurateRip v1  AccurateRip v2");

        int firstNum = audio.First().Number, lastNum = audio.Last().Number;
        var computed = new List<AccurateRip.TrackChecksum>();
        foreach (var t in audio)
        {
            long lengthBytes = (long)t.LengthSectors * t.StoredSectorSize;
            var pcm = new byte[lengthBytes];
            lock (t.Source)
            {
                t.Source.Position = t.SourceByteOffset;
                int read = 0;
                while (read < pcm.Length)
                {
                    int n = t.Source.Read(pcm, read, pcm.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
            }
            var cs = AccurateRip.Compute(pcm, t.Number == firstNum, t.Number == lastNum);
            computed.Add(cs);
            sb.AppendLine($"  {t.Number,2}   {cs.V1:X8}        {cs.V2:X8}");
        }
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(dbPath) && File.Exists(dbPath))
        {
            var chunks = AccurateRipDatabase.Parse(File.ReadAllBytes(dbPath));
            var entries = AccurateRipDatabase.ToEntries(chunks, (id1, id2, cddb));
            if (entries.Count == 0)
            {
                sb.AppendLine("The record holds no pressing matching this disc's IDs.");
                return sb.ToString();
            }
            var result = AccurateRip.Verify(computed, entries);
            sb.AppendLine($"Verification against {chunks.Count} pressing(s):");
            foreach (var v in result.Tracks)
            {
                string mark = v.Status switch
                {
                    AccurateRip.TrackStatus.MatchV2 => $"ACCURATE (v2, confidence {v.Confidence})",
                    AccurateRip.TrackStatus.MatchV1 => $"ACCURATE (v1, confidence {v.Confidence})",
                    _ => "not found / mismatch",
                };
                sb.AppendLine($"  Track {v.TrackIndex + 1,2}: {mark}");
            }
            sb.AppendLine();
            sb.AppendLine(result.Summary);
        }
        else
        {
            sb.AppendLine("Add a downloaded AccurateRip record (from the Lookup URL) to verify.");
        }
        return sb.ToString();
    }
}
