// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Sources;

/// <summary>One line of a source manifest: an on-disc path and where to pull it from.</summary>
public sealed record ManifestEntry(string Path, string Location)
{
    public bool IsUrl => Location.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                      || Location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Parses a plain-text "source manifest" describing files to assemble onto a disc from mixed
/// origins — local paths and HTTP(S) URLs today, cloud drives once those providers are added.
/// Each non-empty, non-comment line is:  <c>&lt;on-disc-path&gt;  &lt;TAB or 2+ spaces&gt;  &lt;location&gt;</c>.
/// A location that is a local directory expands to all files beneath it (keeping their relative
/// paths under the given on-disc path). This is the format the <c>source-stage</c> / <c>disc-span</c>
/// commands accept so a build can span origins the files don't share.
/// </summary>
public static class SourceManifest
{
    public static IReadOnlyList<ManifestEntry> Parse(string text)
    {
        var entries = new List<ManifestEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            // Split on a tab, or on the first run of 2+ spaces (so single spaces in paths survive).
            int sep = line.IndexOf('\t');
            string path, location;
            if (sep >= 0)
            {
                path = line[..sep].Trim();
                location = line[(sep + 1)..].Trim();
            }
            else
            {
                int i = FindDoubleSpace(line);
                if (i < 0) throw new FormatException($"Manifest line is missing a path/location separator: '{line}'");
                path = line[..i].Trim();
                location = line[i..].Trim();
            }
            if (path.Length == 0 || location.Length == 0)
                throw new FormatException($"Manifest line has an empty path or location: '{line}'");
            entries.Add(new ManifestEntry(path.Replace('\\', '/'), location));
        }
        return entries;
    }

    private static int FindDoubleSpace(string s)
    {
        for (int i = 0; i < s.Length - 1; i++)
            if (s[i] == ' ' && s[i + 1] == ' ') return i;
        return -1;
    }
}

/// <summary>An <see cref="IFileSource"/> built from a parsed manifest — local files, whole local
/// directories, and HTTP(S) URLs combined into one addressable set.</summary>
public sealed class ManifestSource : IFileSource
{
    private readonly List<(string path, string location, bool isUrl)> _files = new();

    public ManifestSource(IEnumerable<ManifestEntry> entries)
    {
        foreach (var e in entries)
        {
            if (!e.IsUrl && Directory.Exists(e.Location))
            {
                string root = Path.GetFullPath(e.Location);
                foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                    string onDisc = string.IsNullOrEmpty(e.Path) ? rel : $"{e.Path.TrimEnd('/')}/{rel}";
                    _files.Add((onDisc, f, false));
                }
            }
            else
            {
                _files.Add((e.Path, e.Location, e.IsUrl));
            }
        }
    }

    public string Name => $"manifest:{_files.Count} file(s)";

    public IEnumerable<SourceEntry> Enumerate()
    {
        foreach (var (path, location, isUrl) in _files)
        {
            long size = -1;
            if (!isUrl && File.Exists(location)) size = new FileInfo(location).Length;
            yield return new SourceEntry(path, size);
        }
    }

    public Stream Open(SourceEntry entry)
    {
        var match = _files.FirstOrDefault(f => f.path == entry.Path);
        if (match.location is null) throw new FileNotFoundException($"No source for '{entry.Path}'.");
        if (match.isUrl)
        {
            var http = new HttpFileSource(new[] { (match.path, new Uri(match.location)) });
            return http.Open(entry);
        }
        return File.OpenRead(match.location);
    }
}

/// <summary>Materializes a source into a local staging folder so the image builders (build-raw,
/// iso-create, …) can consume it. Returns what landed and how many bytes.</summary>
public static class SourceStager
{
    public sealed record Result(int Files, long Bytes, IReadOnlyList<string> Failures);

    public static Result Stage(IFileSource source, string stagingDir, IProgress<string>? progress = null)
    {
        Directory.CreateDirectory(stagingDir);
        int files = 0; long bytes = 0;
        var failures = new List<string>();

        foreach (var entry in source.Enumerate())
        {
            string dest = Path.Combine(stagingDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            try
            {
                progress?.Report($"staging {entry.Path}");
                using (var src = source.Open(entry))
                using (var dst = File.Create(dest))
                {
                    src.CopyTo(dst);
                    bytes += dst.Length;   // stream length is correct pre-dispose; FileInfo may be stale
                }
                files++;
            }
            catch (Exception ex)
            {
                failures.Add($"{entry.Path}: {ex.Message}");
            }
        }
        return new Result(files, bytes, failures);
    }
}
