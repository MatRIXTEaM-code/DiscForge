// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.DvdVideo;

/// <summary>
/// Adapters that feed <see cref="IfoReader"/> from the two places a DVD-Video
/// tree actually lives: a <c>VIDEO_TS</c> folder on disk (a ripped or authored
/// DVD), and a filesystem view of a disc image (UDF or ISO 9660). Both expose
/// the same <see cref="IfoReader.IVideoTsSource"/> so the reader is agnostic.
/// </summary>
public static class VideoTsSources
{
    /// <summary>A VIDEO_TS folder on disk.</summary>
    public sealed class Folder : IfoReader.IVideoTsSource
    {
        private readonly string _videoTsDir;

        /// <param name="path">Either the VIDEO_TS folder itself, or its parent
        /// (the disc root); we locate VIDEO_TS under it.</param>
        public Folder(string path)
        {
            if (Directory.Exists(Path.Combine(path, "VIDEO_TS")))
                _videoTsDir = Path.Combine(path, "VIDEO_TS");
            else if (string.Equals(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
                         "VIDEO_TS", StringComparison.OrdinalIgnoreCase))
                _videoTsDir = path;
            else if (File.Exists(Path.Combine(path, "VIDEO_TS.IFO")))
                _videoTsDir = path;
            else
                throw new IfoFormatException($"No VIDEO_TS folder found under '{path}'.");
        }

        public byte[]? ReadFile(string name)
        {
            var p = Resolve(name);
            return p is not null && File.Exists(p) ? File.ReadAllBytes(p) : null;
        }

        public long FileSize(string name)
        {
            var p = Resolve(name);
            return p is not null && File.Exists(p) ? new FileInfo(p).Length : 0;
        }

        // DVD-Video names are upper-case by spec, but disk case varies; match
        // case-insensitively.
        private string? Resolve(string name)
        {
            var direct = Path.Combine(_videoTsDir, name);
            if (File.Exists(direct)) return direct;
            foreach (var f in Directory.EnumerateFiles(_videoTsDir))
                if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase))
                    return f;
            return null;
        }
    }

    /// <summary>
    /// A VIDEO_TS tree inside a filesystem listing already read from an image.
    /// The caller supplies (path → size) plus a reader delegate for the small
    /// IFO files; VOB bytes are taken from the size map (we never load a VOB).
    /// This keeps the adapter independent of whether the image was UDF or ISO.
    /// </summary>
    public sealed class FromListing : IfoReader.IVideoTsSource
    {
        private readonly Dictionary<string, long> _sizes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<string, byte[]?> _readIfo;

        /// <param name="files">Every file path in the image (any casing, any
        /// directory separators); only VIDEO_TS entries are used.</param>
        /// <param name="readIfo">Reads a VIDEO_TS file by its bare name
        /// (e.g. "VTS_01_0.IFO") and returns its bytes, or null.</param>
        public FromListing(IEnumerable<(string Path, long Size)> files, Func<string, byte[]?> readIfo)
        {
            _readIfo = readIfo;
            foreach (var (path, size) in files)
            {
                var name = BareVideoTsName(path);
                if (name is not null) _sizes[name] = size;
            }
        }

        public byte[]? ReadFile(string name) => _readIfo(name);
        public long FileSize(string name) => _sizes.TryGetValue(name, out var s) ? s : 0;

        // Return "VTS_01_0.IFO" from "/VIDEO_TS/VTS_01_0.IFO" (any separator/case),
        // or null if the path isn't in VIDEO_TS.
        private static string? BareVideoTsName(string path)
        {
            var norm = path.Replace('\\', '/');
            int idx = norm.IndexOf("VIDEO_TS/", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            var rest = norm[(idx + "VIDEO_TS/".Length)..];
            return rest.Contains('/') ? null : rest;   // must be directly in VIDEO_TS
        }
    }
}
