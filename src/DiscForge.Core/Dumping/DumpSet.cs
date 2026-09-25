// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text.RegularExpressions;
using DiscForge.Core.Cue;
using DiscForge.Core.Gdi;

namespace DiscForge.Core.Dumping;

/// <summary>
/// The files that make up one dump as another tool left it: a sheet (<c>.cue</c>/<c>.gdi</c>) and
/// every track file it names, plus the dumper's <c>.log</c> and the PS1 subchannel sidecars
/// (<c>.sbi</c>/<c>.sub</c>) when they sit beside it. redumper, for one, writes a <c>.cue</c> and a
/// <c>.bin</c> per track, so "import the image" means importing the set, not one file.
/// </summary>
public sealed record DumpSet
{
    /// <summary>The picked file: the sheet, or a lone image.</summary>
    public required string Primary { get; init; }
    /// <summary>Every file to copy, the primary first. All exist.</summary>
    public required IReadOnlyList<string> Files { get; init; }
    /// <summary>Track files the sheet names that aren't on disk — an incomplete set.</summary>
    public required IReadOnlyList<string> Missing { get; init; }
    /// <summary>The dumper's log, if one was found (also in <see cref="Files"/>).</summary>
    public string? Log { get; init; }

    private static readonly Regex TrackSuffix = new(@"\s*\(Track \d+\)$", RegexOptions.IgnoreCase);

    /// <summary>Resolve the set around <paramref name="picked"/>. A <c>.cue</c>/<c>.gdi</c> brings in
    /// its track files; anything else is a one-file set. Either way the dump's log and sidecars
    /// beside it are included.</summary>
    public static DumpSet Resolve(string picked)
    {
        ArgumentException.ThrowIfNullOrEmpty(picked);
        string full = Path.GetFullPath(picked);
        string dir = Path.GetDirectoryName(full) ?? ".";
        var files = new List<string> { full };
        var missing = new List<string>();

        void AddReferenced(string name)
        {
            string p = Path.GetFullPath(Path.Combine(dir, name));
            if (files.Contains(p, StringComparer.OrdinalIgnoreCase)) return;
            if (File.Exists(p)) files.Add(p); else missing.Add(p);
        }

        string ext = Path.GetExtension(full);
        if (ext.Equals(".cue", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var t in CueSheet.Parse(File.ReadAllText(full)).Tracks) AddReferenced(t.File);
        }
        else if (ext.Equals(".gdi", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var t in GdiParser.ParseFile(full).Tracks) AddReferenced(t.FileName);
        }

        string stem = TrackSuffix.Replace(Path.GetFileNameWithoutExtension(full), "");
        string? log = FindLog(dir, stem);
        if (log is not null && !files.Contains(log, StringComparer.OrdinalIgnoreCase)) files.Add(log);
        foreach (var side in new[] { ".sbi", ".sub" })
        {
            string p = Path.Combine(dir, stem + side);
            if (File.Exists(p) && !files.Contains(p, StringComparer.OrdinalIgnoreCase)) files.Add(p);
        }

        return new DumpSet { Primary = full, Files = files, Missing = missing, Log = log };
    }

    /// <summary><c>&lt;stem&gt;.log</c> beside the dump; failing that, the folder's only .log.</summary>
    public static string? FindLog(string dir, string stem)
    {
        string direct = Path.Combine(dir, stem + ".log");
        if (File.Exists(direct)) return Path.GetFullPath(direct);
        var logs = Directory.GetFiles(dir, "*.log");
        return logs.Length == 1 ? Path.GetFullPath(logs[0]) : null;
    }
}
