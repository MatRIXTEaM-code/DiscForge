// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DiscForge.Core.DvdVideo;

namespace DiscForge.App.Views;

/// <summary>
/// Read a DVD-Video disc's own description of itself: titles, chapters, angles,
/// audio and subtitle streams, and how much video each title set actually holds.
///
/// A DVD declares its structure in plain files. VIDEO_TS.IFO lists the titles;
/// each VTS_nn_0.IFO describes one title set's streams. Only the VOB content is
/// ever scrambled, so reading the structure needs no decryption and no keys —
/// this is a table of contents, and reading one defeats nothing.
///
/// Some discs, though, have a title table that is not a contents listing at all.
/// Structural protection schemes author dozens of decoy titles in title sets
/// whose IFO files exist but whose video files do not, so that software reading
/// the table cannot tell which title is the feature. Presenting such a wall of
/// rows as though it described the disc would be misleading, so it is judged and
/// labelled rather than merely printed.
/// </summary>
internal sealed class DvdInfoView : UserControl
{
    private readonly TextBox _path = new()
    {
        ReadOnly = true, Width = 480, Font = Theme.Ui, Location = new Point(12, 14),
    };
    private readonly Button _browse = new()
    {
        Text = "VIDEO_TS folder…", Location = new Point(500, 13), Width = 116, Height = 24,
        FlatStyle = FlatStyle.System,
    };
    private readonly CheckBox _distinctiveOnly = new()
    {
        Text = "Hide decoy titles", AutoSize = true, Location = new Point(12, 46),
        Font = Theme.Ui, Visible = false,
    };
    private readonly Button _saveLog = new()
    {
        Text = "Save log…", Location = new Point(624, 13), Width = 86, Height = 24,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Label _summary = new()
    {
        AutoSize = false, Location = new Point(12, 70), Size = new Size(712, 22),
        Font = new Font(Theme.Ui.FontFamily, 10f, FontStyle.Bold),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly TextBox _out = new()
    {
        Location = new Point(12, 98), Size = new Size(712, 350),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        BackColor = Color.White, WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 8.5f),
    };

    private OperationLog? _log;
    private IfoReader.DvdStructure? _structure;
    private string? _source;

    public DvdInfoView()
    {
        Size = new Size(736, 470);
        BackColor = Color.White;
        Padding = new Padding(12);

        _browse.Click += async (_, _) => await OpenAsync();
        _saveLog.Click += (_, _) => SaveLog();
        _distinctiveOnly.CheckedChanged += (_, _) => Redisplay();

        Controls.Add(_path); Controls.Add(_browse);
        Controls.Add(_distinctiveOnly); Controls.Add(_saveLog);
        Controls.Add(_summary); Controls.Add(_out);

        _out.Text =
            "Choose a VIDEO_TS folder to see what the disc holds." + Environment.NewLine +
            Environment.NewLine +
            "For a disc in the drive, browse to the drive letter and select the" + Environment.NewLine +
            "VIDEO_TS folder itself — not a file inside it. That works on encrypted" + Environment.NewLine +
            "commercial discs too: the IFO files describing the structure are never" + Environment.NewLine +
            "scrambled, only the video is." + Environment.NewLine +
            Environment.NewLine +
            "A DVD declares how many titles it has, how many chapters in each, and" + Environment.NewLine +
            "which audio and subtitle streams are present in what languages." + Environment.NewLine +
            Environment.NewLine +
            "Some discs declare dozens of decoy titles as a form of copy protection —" + Environment.NewLine +
            "title sets whose IFO files exist but whose video does not. Where that is" + Environment.NewLine +
            "what a table looks like, it is labelled as such rather than presented as" + Environment.NewLine +
            "a contents listing.";
    }

    private void SaveLog()
    {
        if (_log is null) return;
        var path = _log.SaveWithDialog();
        if (path is not null) StatusBus.Report($"Log saved to {Path.GetFileName(path)}");
    }

    private async Task OpenAsync()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Choose a VIDEO_TS folder…",
            SelectedPath = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        string source = dlg.SelectedPath;

        // Selecting the drive root rather than VIDEO_TS is the commonest slip,
        // and stepping into it is friendlier than refusing.
        string videoTs = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
        if (!videoTs.Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase))
        {
            string inside = Path.Combine(source, "VIDEO_TS");
            if (Directory.Exists(inside)) source = inside;
        }

        _path.Text = source;
        _source = source;
        AppSettings.LastImageDirectory = source;

        _summary.Text = "Reading…";
        _summary.ForeColor = Color.Gray;
        _out.Text = "";
        _saveLog.Enabled = false;
        _distinctiveOnly.Visible = false;

        try
        {
            var dir = source;
            _structure = await Task.Run(() => IfoReader.Read(new VideoTsSources.Folder(dir)));
            Redisplay();
        }
        catch (IfoFormatException ex)
        {
            _summary.Text = "Not a DVD-Video disc.";
            _summary.ForeColor = Color.FromArgb(0xA0, 0x60, 0x00);
            _out.Text = ex.Message + Environment.NewLine + Environment.NewLine +
                        "A data DVD, or one holding video in some other form, has no VIDEO_TS" +
                        Environment.NewLine +
                        "structure to read. Use Browse to see its files instead.";
        }
        catch (Exception ex)
        {
            _summary.Text = "Could not read it.";
            _summary.ForeColor = Color.FromArgb(0xA0, 0x20, 0x20);
            _out.Text = ex.Message;
            AppLog.WriteException("dvd structure", ex);
        }
    }

    private void Redisplay()
    {
        if (_structure is null || _source is null) return;

        var finding = StructureAnalysis.Judge(_structure);

        _summary.Text = finding.Summary;
        _summary.ForeColor = finding.Verdict switch
        {
            StructureVerdict.Normal => Color.FromArgb(0x20, 0x70, 0x20),
            StructureVerdict.Unusual => Color.FromArgb(0x60, 0x60, 0x20),
            _ => Color.FromArgb(0xA0, 0x60, 0x00),
        };

        // The filter is only meaningful where there are decoys to hide.
        _distinctiveOnly.Visible = finding.Verdict == StructureVerdict.Obfuscated;

        string text = Render(_structure, _source, finding,
                             _distinctiveOnly.Visible && _distinctiveOnly.Checked);
        _out.Text = text;

        var log = new OperationLog("DVD structure");
        log.Settings(("Source", _source), ("Titles", _structure.Titles.Count),
                     ("Title sets", _structure.TitleSets.Count),
                     ("Video bytes", _structure.TotalVideoBytes),
                     ("Verdict", finding.Verdict));
        log.Result(text);
        _log = log;
        _saveLog.Enabled = true;

        AppLog.Write($"  dvd structure '{Path.GetFileName(_source)}': " +
                     $"{_structure.Titles.Count} title(s), {_structure.TitleSets.Count} set(s), " +
                     $"{finding.Verdict}");
        StatusBus.Report(finding.Summary);
    }

    private static string Render(IfoReader.DvdStructure s, string source,
                                 StructureFinding finding, bool distinctiveOnly)
    {
        var sb = new StringBuilder();

        sb.AppendLine(Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)));
        sb.AppendLine($"  {s.TotalVideoBytes / (1024.0 * 1024 * 1024):N2} GB of video, " +
                      $"{s.TotalMenuBytes / (1024.0 * 1024):N0} MB of menus");
        sb.AppendLine();

        // The verdict first, because on an obfuscated disc it changes how every
        // number below should be read.
        if (finding.Verdict != StructureVerdict.Normal)
        {
            sb.AppendLine(finding.Verdict == StructureVerdict.Obfuscated
                ? "THIS TABLE IS NOT A CONTENTS LISTING"
                : "AN UNUSUAL STRUCTURE");
            sb.AppendLine();
            foreach (var e in finding.Evidence)
            {
                foreach (var line in Wrap(e, 74)) sb.AppendLine("  " + line);
                sb.AppendLine();
            }
        }

        var shown = distinctiveOnly ? StructureAnalysis.Distinctive(s) : s.Titles;

        sb.AppendLine(distinctiveOnly
            ? $"TITLES  (showing {shown.Count} of {s.Titles.Count} — decoys hidden)"
            : "TITLES");
        sb.AppendLine("  #   Chapters  Angles  Set   Video in that set");
        sb.AppendLine("  --  --------  ------  ---   -----------------");
        foreach (var t in shown)
        {
            var set = s.TitleSets.FirstOrDefault(x => x.Number == t.TitleSet);
            string video = set is null
                ? "—"
                : set.TitleVobBytes == 0
                    ? "none"
                    : $"{set.TitleVobBytes / (1024.0 * 1024 * 1024):N2} GB";

            sb.AppendLine($"  {t.TitleNumber,2}  {t.Chapters,8}  {t.AngleCount,6}  " +
                          $"{t.TitleSet,3}   {video}");
        }
        sb.AppendLine();

        if (finding.Verdict == StructureVerdict.Obfuscated && !distinctiveOnly)
        {
            sb.AppendLine("Tick \"Hide decoy titles\" to narrow this to the titles whose set holds");
            sb.AppendLine("video — the rest cannot play anything whatever they claim.");
            sb.AppendLine();
        }
        else if (finding.Verdict == StructureVerdict.Normal && s.Titles.Count > 1)
        {
            var longest = s.Titles.OrderByDescending(t => t.Chapters).First();
            sb.AppendLine($"Title {longest.TitleNumber} has the most chapters — on most discs that");
            sb.AppendLine("is the main feature, with the rest being extras, trailers or menus.");
            sb.AppendLine();
        }

        foreach (var set in s.TitleSets)
        {
            sb.AppendLine($"TITLE SET {set.Number}" +
                          (set.TitleVobBytes == 0 ? "   (no video — decoy)" : ""));
            sb.AppendLine($"  Video      {set.TitleVobBytes / (1024.0 * 1024):N0} MB" +
                          (set.MenuVobBytes > 0
                              ? $", menu {set.MenuVobBytes / (1024.0 * 1024):N0} MB"
                              : ""));
            sb.AppendLine($"  Titles     {set.Titles.Count}");

            // Streams are declared per title within the set, and are usually
            // identical across them — so the first title's are representative.
            var first = set.Titles.FirstOrDefault();
            if (first is not null)
            {
                if (first.Audio.Count > 0)
                {
                    sb.AppendLine($"  Audio      {first.Audio.Count} stream(s)");
                    foreach (var a in first.Audio)
                        sb.AppendLine($"    {a}");
                }
                else
                {
                    sb.AppendLine("  Audio      none declared");
                }

                if (first.Subtitles.Count > 0)
                {
                    sb.AppendLine($"  Subtitles  {first.Subtitles.Count} stream(s)");
                    foreach (var t in first.Subtitles)
                        sb.AppendLine($"    {t}");
                }
                else
                {
                    sb.AppendLine("  Subtitles  none");
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine("Read from the disc's own IFO files, which are never encrypted — this is");
        sb.AppendLine("its table of contents, not its content.");
        return sb.ToString();
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var words = text.Split(' ');
        var line = new StringBuilder();
        foreach (var w in words)
        {
            if (line.Length > 0 && line.Length + 1 + w.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(w);
        }
        if (line.Length > 0) yield return line.ToString();
    }
}