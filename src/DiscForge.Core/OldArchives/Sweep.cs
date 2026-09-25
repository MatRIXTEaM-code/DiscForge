// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;
using DiscForge.Core.Files;

namespace DiscForge.Core.OldArchives;

/// <summary>The outcome for one archive in a folder sweep or a disc image.</summary>
public sealed record SweepItem(
    string Path,
    string? Format,
    string Status,
    int Files,
    long Bytes,
    int Failed,
    string Detail,
    string? ExtractedTo = null)
{
    public const string StatusOk = "OK", StatusDamaged = "damaged", StatusPassword = "needs password",
        StatusMissingVolume = "missing volume", StatusUnreadable = "can't read";

    public bool Ok => Status == StatusOk;
}

public sealed record SweepOptions
{
    /// <summary>Also look inside .exe/.com files for self-extracting archives.</summary>
    public bool IncludeExecutables { get; init; } = true;
    public bool Recursive { get; init; } = true;
    public string? Password { get; init; }
    /// <summary>When set, every good archive is extracted under this folder (mirroring the source
    /// layout, one sub-folder per archive).</summary>
    public string? ExtractTo { get; init; }
    public bool Overwrite { get; init; }
}

/// <summary>
/// Finds every ACE, LHA/LZH, ARJ and ZOO archive in a folder (multi-volume sets counted once,
/// self-extracting .exe files included), tests each one and optionally extracts the good ones.
/// </summary>
public static class ArchiveSweep
{
    /// <summary>Archives under <paramref name="folder"/>, one path per archive or volume set.</summary>
    public static List<string> FindArchives(string folder, SweepOptions? options = null)
    {
        options ??= new SweepOptions();
        var opt = new EnumerationOptions { RecurseSubdirectories = options.Recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var all = Directory.EnumerateFiles(folder, "*", opt).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        return PickArchives(all, p =>
        {
            string e = Path.GetExtension(p).ToLowerInvariant();
            if (OldArchive.HasArchiveExtension(p)) return true;
            if (options.IncludeExecutables && e is ".exe" or ".com" or ".run" or ".sfx")
                return new FileInfo(p).Length < 512L * 1024 * 1024 && OldArchive.Detect(p) is not null;
            return false;
        });
    }

    /// <summary>From a list of paths keep the archives, counting each multi-volume set once (by its
    /// first volume when present, else its lowest-numbered one).</summary>
    public static List<string> PickArchives(IEnumerable<string> paths, Func<string, bool> isCandidate)
    {
        var list = paths.Where(isCandidate).ToList();
        var firsts = new HashSet<string>(list.Where(p => !OldArchive.IsNumberedVolume(p))
            .Select(p => StemKey(p)), StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var seenVolumeSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in list)
        {
            if (OldArchive.IsNumberedVolume(p))
            {
                string key = StemKey(p);
                if (firsts.Contains(key)) continue;          // the .ace/.arj stands for the set
                if (!seenVolumeSets.Add(key)) continue;      // only the lowest-numbered volume
            }
            result.Add(p);
        }
        return result;
    }

    private static string StemKey(string p) => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(p) ?? "", System.IO.Path.GetFileNameWithoutExtension(p));

    /// <summary>Test (and optionally extract) one archive.</summary>
    public static SweepItem Check(string path, SweepOptions options, string? extractFolder = null, CancellationToken ct = default)
    {
        OldArchive a;
        try { a = OldArchive.Open(path); }
        catch (Exception ex) when (ex is OldArchiveException or Ace.AceFormatException or IOException or UnauthorizedAccessException)
        {
            return new SweepItem(path, OldArchive.Detect(path), SweepItem.StatusUnreadable, 0, 0, 0, ex.Message);
        }
        using (a)
        {
            OldRunResult r;
            string? outDir = null;
            if (extractFolder is not null)
            {
                outDir = extractFolder;
                r = a.ExtractAll(outDir, options.Password, options.Overwrite, null, ct);
            }
            else r = a.TestAll(options.Password, null, ct);

            int files = a.Entries.Count(e => !e.IsDirectory);
            long bytes = a.Entries.Sum(e => e.Size);
            var failed = r.Entries.Where(e => !e.Ok).ToList();
            string status;
            bool truncated = a.Warnings.Any(w => w.Contains("truncated") || w.Contains("Stopped") || w.Contains("stopped"));
            bool missingVolume = a.Warnings.Any(w => w.Contains("isn't here") || w.Contains("is missing"));
            if (failed.Count == 0) status = missingVolume ? SweepItem.StatusMissingVolume : truncated || a.Entries.Count == 0 ? SweepItem.StatusDamaged : SweepItem.StatusOk;
            else if (failed.All(f => f.Error?.Contains("password", StringComparison.OrdinalIgnoreCase) == true)) status = SweepItem.StatusPassword;
            else if (failed.Any(f => f.Error?.Contains("volume") == true) || a.Warnings.Any(w => w.Contains("isn't here"))) status = SweepItem.StatusMissingVolume;
            else if (failed.All(f => f.Error?.Contains("already exists") == true)) status = SweepItem.StatusOk;
            else status = SweepItem.StatusDamaged;

            var detail = new StringBuilder(a.Description);
            if (failed.Count > 0)
            {
                detail.Append($"; {failed.Count} problem(s), first: {failed[0].Entry.Name}: {failed[0].Error}");
            }
            foreach (var w in a.Warnings.Take(2)) detail.Append("; ").Append(w);
            return new SweepItem(path, a.Format, status, files, bytes, failed.Count, detail.ToString(), outDir);
        }
    }

    /// <summary>Find, test and optionally extract every archive under <paramref name="folder"/>.</summary>
    public static List<SweepItem> Run(string folder, SweepOptions options,
        IProgress<(int Done, int Total, string Name)>? progress = null, CancellationToken ct = default)
    {
        var archives = FindArchives(folder, options);
        var items = new List<SweepItem>();
        for (int i = 0; i < archives.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string p = archives[i];
            progress?.Report((i, archives.Count, p));
            string? dest = null;
            if (options.ExtractTo is not null)
            {
                string rel = System.IO.Path.GetRelativePath(folder, System.IO.Path.GetDirectoryName(p)!);
                string leaf = System.IO.Path.GetFileNameWithoutExtension(p);
                dest = System.IO.Path.Combine(options.ExtractTo, rel == "." ? "" : rel, leaf);
            }
            items.Add(Check(p, options, dest, ct));
        }
        progress?.Report((archives.Count, archives.Count, ""));
        return items;
    }

    public static string ToCsv(IEnumerable<SweepItem> items, string? baseFolder = null)
    {
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("Archive,Format,Status,Files,Bytes,Problems,Details,ExtractedTo\r\n");
        foreach (var i in items)
            sb.Append(Q(baseFolder is null ? i.Path : System.IO.Path.GetRelativePath(baseFolder, i.Path))).Append(',')
              .Append(i.Format ?? "").Append(',').Append(Q(i.Status)).Append(',').Append(i.Files).Append(',')
              .Append(i.Bytes).Append(',').Append(i.Failed).Append(',').Append(Q(i.Detail)).Append(',')
              .Append(Q(i.ExtractedTo ?? "")).Append("\r\n");
        return sb.ToString();
    }

    public static string Summary(IReadOnlyCollection<SweepItem> items)
    {
        int ok = items.Count(i => i.Ok);
        var groups = items.Where(i => !i.Ok).GroupBy(i => i.Status).Select(g => $"{g.Count()} {g.Key}");
        return $"{items.Count} archive(s): {ok} OK" + (items.Count > ok ? ", " + string.Join(", ", groups) : "") + ".";
    }
}

/// <summary>Old archives stored inside a disc image (ISO 9660 / UDF, bin/cue, CDI …).</summary>
public static class DiscImageArchives
{
    public sealed record Found(string PathInImage, long Size, IReadOnlyList<string> VolumePathsInImage);

    /// <summary>Archive files on the disc, by name (.ace/.lzh/.lha/.arj/.zoo and their volumes).</summary>
    public static (List<Found> Archives, string? Error) Find(string imagePath)
    {
        var listing = ImageBrowser.List(imagePath);
        if (listing.Error is not null) return (new List<Found>(), listing.Error);
        var files = listing.Files.ToDictionary(f => f.Path, f => f.Size, StringComparer.Ordinal);
        var picked = ArchiveSweep.PickArchives(listing.Files.Select(f => f.Path), OldArchive.HasArchiveExtension);
        var result = new List<Found>();
        foreach (var p in picked)
        {
            string stem = StemOf(p);
            var vols = listing.Files.Select(f => f.Path)
                .Where(q => OldArchive.IsNumberedVolume(q) && string.Equals(StemOf(q), stem, StringComparison.OrdinalIgnoreCase))
                .OrderBy(q => q, StringComparer.OrdinalIgnoreCase).ToList();
            var all = new List<string> { p };
            all.AddRange(vols.Where(v => v != p));
            result.Add(new Found(p, all.Sum(q => files[q]), all));
        }
        return (result, null);
    }

    private static string StemOf(string p)
    {
        int slash = p.LastIndexOf('/');
        int dot = p.LastIndexOf('.');
        return dot > slash ? p[..dot] : p;
    }

    /// <summary>Copy an archive (and its volumes) out of the image into a new temporary folder and
    /// return the local path of its first file. Delete <paramref name="tempDir"/> when done.</summary>
    public static string CopyOut(string imagePath, Found f, out string tempDir)
    {
        tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "discforge-arc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var entries = f.VolumePathsInImage.Select(p => new ImageBrowser.FileEntry(p, 0)).ToList();
        var r = ImageBrowser.Extract(imagePath, entries, tempDir);
        if (r.Failed > 0) throw new IOException("Couldn't copy the archive out of the image: " + string.Join("; ", r.Problems));
        return SafeExtract.ContainedPath(tempDir, f.PathInImage.TrimStart('/'));
    }

    /// <summary>Test or extract every archive found in the image. With <paramref name="outDir"/>, each
    /// archive goes to outDir/&lt;its path on the disc without the extension&gt;/.</summary>
    public static List<SweepItem> Process(string imagePath, string? outDir, string? password, bool overwrite,
        IProgress<(int Done, int Total, string Name)>? progress = null, CancellationToken ct = default)
    {
        var (found, error) = Find(imagePath);
        if (error is not null) throw new IOException(error);
        var items = new List<SweepItem>();
        for (int i = 0; i < found.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var f = found[i];
            progress?.Report((i, found.Count, f.PathInImage));
            string? temp = null;
            try
            {
                string local = CopyOut(imagePath, f, out temp);
                string? dest = null;
                if (outDir is not null)
                {
                    string rel = SafeExtract.SanitizeRelativePath(StemOf(f.PathInImage));
                    dest = SafeExtract.ContainedPath(outDir, rel.Length > 0 ? rel : $"archive{i}");
                }
                var item = ArchiveSweep.Check(local, new SweepOptions { Password = password, Overwrite = overwrite }, dest, ct);
                items.Add(item with { Path = f.PathInImage });
            }
            catch (Exception ex) when (ex is IOException or OldArchiveException or UnauthorizedAccessException)
            {
                items.Add(new SweepItem(f.PathInImage, null, SweepItem.StatusUnreadable, 0, 0, 0, ex.Message));
            }
            finally
            {
                if (temp is not null) try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        progress?.Report((found.Count, found.Count, ""));
        return items;
    }
}
