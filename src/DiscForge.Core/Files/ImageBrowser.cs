// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cdi;
using DiscForge.Core.Hfs;
using DiscForge.Core.Iso;
using DiscForge.Core.Udf;

namespace DiscForge.Core.Files;

/// <summary>
/// Lists and extracts the files inside a disc image, whichever filesystem it
/// happens to carry.
///
/// A disc may hold ISO 9660, UDF, or both — a "UDF bridge" disc has the same
/// content described twice so that older and newer systems can each read it.
/// Where both are present UDF is preferred, because ISO 9660 Level 1 mangles
/// names into uppercase 8.3 while UDF keeps them as the author wrote them:
/// readme.txt rather than README.TXT, and long names intact rather than
/// truncated. Which one was used is reported either way, so nothing is hidden.
///
/// The CDI case needs one extra step: an image's tracks are stored raw, and
/// where the user data begins within each sector depends on the track's mode —
/// offset 16 for Mode 1, 24 for Mode 2 Form 1, after its sub-header.
/// CdiUserDataStream handles that, but only if the descriptor records the mode
/// correctly. An image written before DiscForge probed modes from the disc says
/// Mode 1 for everything, reads eight bytes early, and appears to have no
/// filesystem at all — which is what `dforge fix-modes` exists to repair.
/// </summary>
public static class ImageBrowser
{
    public sealed record FileEntry(string Path, long Size);

    public sealed record Listing
    {
        public required IReadOnlyList<FileEntry> Files { get; init; }
        public required string Filesystem { get; init; }
        public string? VolumeId { get; init; }
        /// <summary>Set when nothing could be listed, with an explanation.</summary>
        public string? Error { get; init; }
        /// <summary>True when the image carries both filesystems. UDF was used;
        /// the ISO 9660 view of the same content is also present.</summary>
        public bool BridgeDisc { get; init; }

        public long TotalBytes => Files.Sum(f => f.Size);
    }

    public sealed record ExtractionResult
    {
        public required int Extracted { get; init; }
        public required int Failed { get; init; }
        public required long BytesWritten { get; init; }
        public required IReadOnlyList<string> Problems { get; init; }
    }

    /// <summary>List every file in the image.</summary>
    public static Listing List(string imagePath)
    {
        ArgumentNullException.ThrowIfNull(imagePath);
        if (!File.Exists(imagePath))
            return Empty($"File not found: {imagePath}");

        try
        {
            using var opened = Open(imagePath);
            if (opened.Error is not null) return Empty(opened.Error);

            var view = opened.View!;

            // UDF first where it exists: on a bridge disc both describe the same
            // content, but ISO 9660 Level 1 uppercases and truncates to 8.3
            // while UDF preserves the real names.
            view.Position = 0;
            bool hasUdf = UdfReader.IsUdf(view);

            if (hasUdf)
            {
                try
                {
                    view.Position = 0;
                    var vol = UdfReader.Read(view);
                    var udfFiles = vol.Files
                        .Select(e => new FileEntry(e.Path, e.Size))
                        .OrderBy(f => f.Path, StringComparer.Ordinal)
                        .ToList();

                    bool bridge = HasIso9660(view);
                    return new Listing
                    {
                        Files = udfFiles,
                        Filesystem = bridge ? "UDF (bridge disc; ISO 9660 also present)" : "UDF",
                        VolumeId = vol.VolumeId,
                        BridgeDisc = bridge,
                    };
                }
                catch (UdfFormatException)
                {
                    // Declared but unreadable. ISO 9660 may still be there and
                    // intact, which beats reporting nothing.
                }
            }

            try
            {
                view.Position = 0;
                var dir = IsoReader.Read(view, IsoReader.NamePreference.Auto);
                var files = dir.Files
                    .Select(e => new FileEntry(e.Path, e.Size))
                    .OrderBy(f => f.Path, StringComparer.Ordinal)
                    .ToList();

                string kind = dir.Joliet ? "ISO 9660 + Joliet"
                            : dir.RockRidge ? "ISO 9660 + Rock Ridge"
                            : "ISO 9660";
                if (hasUdf) kind += " (UDF present but unreadable)";

                return new Listing { Files = files, Filesystem = kind, VolumeId = dir.VolumeId };
            }
            catch (IsoFormatException)
            {
                // Neither worked.
            }

            return Empty("This image has neither an ISO 9660 nor a UDF filesystem. " +
                         "If it is a CDI written before track modes were detected, " +
                         "'dforge fix-modes' may repair it.");
        }
        catch (Exception ex)
        {
            return Empty("Could not read the image: " + ex.Message);
        }
    }

    private static bool HasIso9660(Stream view)
    {
        try
        {
            view.Position = 0;
            IsoReader.Read(view, IsoReader.NamePreference.Auto);
            return true;
        }
        catch (IsoFormatException) { return false; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Extract files to a directory, recreating the image's folder structure.
    /// </summary>
    /// <param name="singleTarget">
    /// When extracting exactly one file, the full path to write it to — so a
    /// user who picked "Save As" gets the name they chose rather than the one
    /// the disc happens to use.
    /// </param>
    public static ExtractionResult Extract(string imagePath,
                                           IReadOnlyList<FileEntry> files,
                                           string outputDirectory,
                                           string? singleTarget = null,
                                           IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(outputDirectory);

        int extracted = 0, failed = 0;
        long bytes = 0;
        var problems = new List<string>();

        using var opened = Open(imagePath);
        if (opened.Error is not null)
            return new ExtractionResult
            {
                Extracted = 0, Failed = files.Count, BytesWritten = 0,
                Problems = new[] { opened.Error },
            };

        var view = opened.View!;
        Directory.CreateDirectory(outputDirectory);

        // Resolve the entries once, from whichever filesystem List would have
        // chosen — otherwise a path listed from UDF wouldn't be found in the
        // ISO 9660 tree, where the same file is called something else.
        IsoDirectory? iso = null;
        UdfVolume? udf = null;

        view.Position = 0;
        if (UdfReader.IsUdf(view))
        {
            try
            {
                view.Position = 0;
                udf = UdfReader.Read(view);
            }
            catch (UdfFormatException) { }
        }

        if (udf is null)
        {
            try
            {
                view.Position = 0;
                iso = IsoReader.Read(view, IsoReader.NamePreference.Auto);
            }
            catch (IsoFormatException) { }
        }

        if (iso is null && udf is null)
            return new ExtractionResult
            {
                Extracted = 0, Failed = files.Count, BytesWritten = 0,
                Problems = new[] { "No readable filesystem in the image." },
            };

        for (int i = 0; i < files.Count; i++)
        {
            var wanted = files[i];
            try
            {
                string target = singleTarget is not null && files.Count == 1
                    ? singleTarget
                    : SafeTarget(outputDirectory, wanted.Path)
                      ?? throw new IOException("the path escapes the output directory");

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var os = File.Create(target);

                if (udf is not null)
                {
                    var entry = udf.Files.FirstOrDefault(e => e.Path == wanted.Path)
                        ?? throw new FileNotFoundException("not present in the image");
                    UdfReader.ExtractFile(view, udf, entry, os);
                }
                else
                {
                    var entry = iso!.Files.FirstOrDefault(e => e.Path == wanted.Path)
                        ?? throw new FileNotFoundException("not present in the image");
                    IsoReader.ExtractFile(view, entry, os);
                }

                extracted++;
                bytes += os.Length;
            }
            catch (Exception ex)
            {
                failed++;
                problems.Add($"{wanted.Path}: {ex.Message}");
            }

            progress?.Report((double)(i + 1) / files.Count);
        }

        return new ExtractionResult
        {
            Extracted = extracted,
            Failed = failed,
            BytesWritten = bytes,
            Problems = problems,
        };
    }

    /// <summary>
    /// Cross-check every filesystem view a disc carries. A bridge/hybrid disc describes
    /// the same files through two independent directory structures (ISO 9660 and UDF);
    /// this reads each one, hashes every file's content, and confirms the views deliver
    /// the same bytes. Content hashing — not name matching — is used, so ISO 9660's 8.3
    /// name mangling (README.TXT) versus UDF's real names (readme.txt) never trips it up.
    /// A divergence means a file reachable from one filesystem but not the other, a size
    /// or content mismatch, or a filesystem that is declared but truncated/corrupt: the
    /// fingerprints of a bad dump, a tampered image, or content hidden from one view.
    /// Pure inspection — it reads and compares, and defeats nothing.
    /// </summary>
    public static CrossCheckResult CrossCheck(string imagePath)
    {
        ArgumentNullException.ThrowIfNull(imagePath);
        if (!File.Exists(imagePath))
            return new CrossCheckResult
            {
                Verdict = CrossCheckVerdict.None,
                Views = Array.Empty<CrossCheckView>(),
                Discrepancies = new[] { new CrossCheckDiscrepancy("open", $"File not found: {imagePath}") },
            };

        using var opened = Open(imagePath);
        if (opened.Error is not null)
            return new CrossCheckResult
            {
                Verdict = CrossCheckVerdict.None,
                Views = Array.Empty<CrossCheckView>(),
                Discrepancies = new[] { new CrossCheckDiscrepancy("open", opened.Error) },
            };

        var view = opened.View!;
        var views = new List<CrossCheckView>();
        var discrepancies = new List<CrossCheckDiscrepancy>();

        // ---- ISO 9660 side (8.3 and, when present, Joliet) --------------------
        List<HashedFile>? isoFiles = null;
        List<HashedFile>? jolietFiles = null;
        string? isoVolumeId = null;
        try
        {
            view.Position = 0;
            var iso = IsoReader.Read(view, IsoReader.NamePreference.Iso9660);
            isoVolumeId = iso.VolumeId;
            isoFiles = HashIso(view, iso);
            views.Add(MakeView("ISO 9660 (8.3)", iso.VolumeId, isoFiles));

            // A separate Joliet name hierarchy, if the disc has one.
            view.Position = 0;
            var jol = IsoReader.Read(view, IsoReader.NamePreference.Joliet);
            if (jol.Joliet)
            {
                jolietFiles = HashIso(view, jol);
                views.Add(MakeView("Joliet", jol.VolumeId, jolietFiles));
            }
        }
        catch (IsoFormatException) { }
        catch (Exception ex) { discrepancies.Add(new CrossCheckDiscrepancy("iso", ex.Message)); }

        // ---- UDF side ---------------------------------------------------------
        List<HashedFile>? udfFiles = null;
        bool udfDeclared = false;
        try
        {
            view.Position = 0;
            udfDeclared = UdfReader.IsUdf(view);
            if (udfDeclared)
            {
                view.Position = 0;
                var vol = UdfReader.Read(view);
                udfFiles = HashUdf(view, vol);
                views.Add(MakeView("UDF", vol.VolumeId, udfFiles));
            }
        }
        catch (Exception ex)
        {
            // Declared on the disc but unreadable — the classic truncated-dump signature.
            views.Add(new CrossCheckView { Kind = "UDF", VolumeId = null, FileCount = 0, TotalBytes = 0, Error = ex.Message });
        }

        // ---- HFS side (Mac+PC hybrid discs) -----------------------------------
        // The HFS structures live in the raw image (located via the volume's Master Directory Block),
        // not in the cooked ISO 9660 data view. A Mac+PC hybrid legitimately carries its own Mac files,
        // so HFS is CATALOGUED against the ISO side (shared / Mac-only / PC-only) and never flips the
        // ISO-vs-UDF bridge verdict.
        TryHfs(imagePath, isoFiles, views, discrepancies);

        // ---- verdict ----------------------------------------------------------
        // The 8.3 and Joliet name spaces are two views of ONE filesystem pointing at the
        // same data, so they must always agree; a mismatch is a malformed disc.
        if (isoFiles is not null && jolietFiles is not null)
        {
            var (onlyA, onlyB) = ContentDiff(isoFiles, jolietFiles);
            if (onlyA.Count > 0 || onlyB.Count > 0)
                discrepancies.Add(new CrossCheckDiscrepancy("iso-vs-joliet",
                    $"the 8.3 and Joliet name spaces of the same ISO 9660 filesystem describe different content " +
                    $"({onlyA.Count} only in 8.3, {onlyB.Count} only in Joliet) — the disc is malformed"));
        }

        CrossCheckVerdict verdict;
        if (udfDeclared && udfFiles is null)
        {
            verdict = CrossCheckVerdict.Incomplete;
            discrepancies.Add(new CrossCheckDiscrepancy("udf",
                "a UDF filesystem is declared on the disc but could not be read — the dump may be truncated or corrupt"));
        }
        else if (isoFiles is not null && udfFiles is not null)
        {
            var (onlyIso, onlyUdf) = ContentDiff(isoFiles, udfFiles);
            if (onlyIso.Count == 0 && onlyUdf.Count == 0)
                verdict = CrossCheckVerdict.Agree;
            else
            {
                verdict = CrossCheckVerdict.Divergent;
                foreach (var f in onlyIso.Take(50))
                    discrepancies.Add(new CrossCheckDiscrepancy("only-in-iso",
                        $"{f.Path} ({f.Size:N0} bytes) is reachable from ISO 9660 but not from UDF"));
                foreach (var f in onlyUdf.Take(50))
                    discrepancies.Add(new CrossCheckDiscrepancy("only-in-udf",
                        $"{f.Path} ({f.Size:N0} bytes) is reachable from UDF but not from ISO 9660"));
            }
        }
        else if (isoFiles is not null || udfFiles is not null)
        {
            verdict = CrossCheckVerdict.Single;
        }
        else
        {
            verdict = CrossCheckVerdict.None;
        }

        return new CrossCheckResult { Verdict = verdict, Views = views, Discrepancies = discrepancies };
    }

    /// <summary>
    /// Build a content index of an image's files — path, size and SHA-256 — from the filesystem
    /// view a reader would present (UDF where readable, otherwise ISO 9660 with its best names).
    /// This is the raw material for comparing two discs at the file level. Read-only.
    /// </summary>
    public static ContentIndex BuildContentIndex(string imagePath)
    {
        ArgumentNullException.ThrowIfNull(imagePath);
        if (!File.Exists(imagePath))
            return new ContentIndex { Error = $"File not found: {imagePath}", Filesystem = "", VolumeId = null, Files = Array.Empty<IndexedFile>() };

        using var opened = Open(imagePath);
        if (opened.Error is not null)
            return new ContentIndex { Error = opened.Error, Filesystem = "", VolumeId = null, Files = Array.Empty<IndexedFile>() };

        var view = opened.View!;
        try
        {
            view.Position = 0;
            if (UdfReader.IsUdf(view))
            {
                try
                {
                    view.Position = 0;
                    var vol = UdfReader.Read(view);
                    return Index(HashUdf(view, vol), HasIso9660(view) ? "UDF (bridge)" : "UDF", vol.VolumeId);
                }
                catch (UdfFormatException) { }
            }

            view.Position = 0;
            var iso = IsoReader.Read(view, IsoReader.NamePreference.Auto);
            string kind = iso.Joliet ? "ISO 9660 + Joliet" : iso.RockRidge ? "ISO 9660 + Rock Ridge" : "ISO 9660";
            return Index(HashIso(view, iso), kind, iso.VolumeId);
        }
        catch (IsoFormatException)
        {
            return new ContentIndex { Error = "No readable ISO 9660 or UDF filesystem in the image.", Filesystem = "", VolumeId = null, Files = Array.Empty<IndexedFile>() };
        }
        catch (Exception ex)
        {
            return new ContentIndex { Error = "Could not read the image: " + ex.Message, Filesystem = "", VolumeId = null, Files = Array.Empty<IndexedFile>() };
        }
    }

    private static ContentIndex Index(List<HashedFile> files, string kind, string? volumeId) =>
        new()
        {
            Filesystem = kind,
            VolumeId = volumeId,
            Files = files.Select(f => new IndexedFile(f.Path, f.Size, f.Sha)).ToList(),
        };

    private readonly record struct HashedFile(string Path, long Size, string Sha);

    /// <summary>
    /// Catalogue the HFS (Mac) side of a hybrid disc against the ISO 9660 side. Mac+PC hybrids
    /// legitimately carry Mac-only files (applications, Finder metadata), so this reports a shared /
    /// Mac-only / PC-only breakdown as information — it never turns an expected hybrid difference into a
    /// DIVERGENT verdict.
    /// </summary>
    private static void TryHfs(string imagePath, List<HashedFile>? isoFiles,
                               List<CrossCheckView> views, List<CrossCheckDiscrepancy> discrepancies)
    {
        byte[] raw;
        try { raw = File.ReadAllBytes(imagePath); }
        catch { return; }
        if (!HfsReader.IsHfs(raw)) return;

        HfsVolume vol;
        try { vol = HfsReader.Read(raw); }
        catch (Exception ex)
        {
            views.Add(new CrossCheckView { Kind = "HFS (Mac)", VolumeId = null, FileCount = 0, TotalBytes = 0, Error = ex.Message });
            return;
        }

        var hfsFiles = new List<HashedFile>();
        int unreadable = 0;
        foreach (var e in vol.Files)
        {
            try
            {
                var bytes = HfsReader.ReadDataFork(raw, vol, e);
                string sha = System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                hfsFiles.Add(new HashedFile(e.Path, e.DataSize, sha));
            }
            catch (HfsFormatException) { unreadable++; }
        }

        views.Add(new CrossCheckView
        {
            Kind = "HFS (Mac)", VolumeId = vol.VolumeName,
            FileCount = hfsFiles.Count, TotalBytes = hfsFiles.Sum(f => f.Size),
        });
        if (unreadable > 0)
            discrepancies.Add(new CrossCheckDiscrepancy("hfs",
                $"{unreadable} HFS file(s) were fragmented beyond the catalog's extents and were not hashed."));

        // Catalogue against the ISO 9660 side, by content.
        if (isoFiles is not null)
        {
            var (onlyIso, onlyHfs) = ContentDiff(isoFiles, hfsFiles);
            int shared = hfsFiles.Count - onlyHfs.Count;
            discrepancies.Add(new CrossCheckDiscrepancy("hybrid",
                $"Mac+PC hybrid disc: {shared} file(s) shared between HFS and ISO 9660, " +
                $"{onlyHfs.Count} Mac-only, {onlyIso.Count} PC-only (a Mac side differing from the PC side is normal, not a fault)."));
            foreach (var f in onlyHfs.Take(50))
                discrepancies.Add(new CrossCheckDiscrepancy("mac-only", $"{f.Path} ({f.Size:N0} bytes) exists only on the HFS (Mac) side."));
        }
    }

    private static CrossCheckView MakeView(string kind, string? volumeId, List<HashedFile> files) =>
        new()
        {
            Kind = kind, VolumeId = volumeId,
            FileCount = files.Count, TotalBytes = files.Sum(f => f.Size),
        };

    private static List<HashedFile> HashIso(Stream view, IsoDirectory iso)
    {
        var list = new List<HashedFile>();
        foreach (var e in iso.Files)
        {
            using var h = new HashingSink();
            view.Position = 0;
            IsoReader.ExtractFile(view, e, h);
            list.Add(new HashedFile(e.Path, e.Size, h.HexDigest()));
        }
        return list;
    }

    private static List<HashedFile> HashUdf(Stream view, UdfVolume vol)
    {
        var list = new List<HashedFile>();
        foreach (var e in vol.Files)
        {
            using var h = new HashingSink();
            view.Position = 0;
            UdfReader.ExtractFile(view, vol, e, h);
            list.Add(new HashedFile(e.Path, e.Size, h.HexDigest()));
        }
        return list;
    }

    /// <summary>Content-multiset difference: files (by size+hash) present in only one side.</summary>
    private static (List<HashedFile> OnlyA, List<HashedFile> OnlyB) ContentDiff(
        List<HashedFile> a, List<HashedFile> b)
    {
        string Key(HashedFile f) => f.Size + ":" + f.Sha;
        var countB = new Dictionary<string, int>();
        foreach (var f in b) countB[Key(f)] = countB.GetValueOrDefault(Key(f)) + 1;
        var onlyA = new List<HashedFile>();
        foreach (var f in a)
        {
            var k = Key(f);
            if (countB.GetValueOrDefault(k) > 0) countB[k]--;
            else onlyA.Add(f);
        }
        var countA = new Dictionary<string, int>();
        foreach (var f in a) countA[Key(f)] = countA.GetValueOrDefault(Key(f)) + 1;
        var onlyB = new List<HashedFile>();
        foreach (var f in b)
        {
            var k = Key(f);
            if (countA.GetValueOrDefault(k) > 0) countA[k]--;
            else onlyB.Add(f);
        }
        return (onlyA, onlyB);
    }

    /// <summary>A write-only stream that keeps a running SHA-256 without holding the data.</summary>
    private sealed class HashingSink : Stream
    {
        private readonly System.Security.Cryptography.IncrementalHash _hash =
            System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        public override void Write(byte[] buffer, int offset, int count) => _hash.AppendData(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _hash.AppendData(buffer);
        public string HexDigest() => System.Convert.ToHexString(_hash.GetHashAndReset());
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// A readable view of the image's filesystem area, and the streams that must
    /// outlive it. A plain .iso is its own view; a .cdi needs its data track
    /// located and its user bytes unwrapped.
    /// </summary>
    private sealed class OpenedImage : IDisposable
    {
        public Stream? View { get; init; }
        public string? Error { get; init; }
        private readonly List<IDisposable> _owned = new();

        public void Own(IDisposable d) => _owned.Add(d);

        public void Dispose()
        {
            // Reverse order: the view wraps the file, and disposing the file
            // first would leave the view reading a closed handle.
            for (int i = _owned.Count - 1; i >= 0; i--)
            {
                try { _owned[i].Dispose(); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
            _owned.Clear();
        }
    }

    private static OpenedImage Open(string imagePath)
    {
        string ext = Path.GetExtension(imagePath);

        if (ext.Equals(".iso", StringComparison.OrdinalIgnoreCase))
        {
            var fs = File.OpenRead(imagePath);
            var img = new OpenedImage { View = fs };
            img.Own(fs);
            return img;
        }

        if (ext.Equals(".cdi", StringComparison.OrdinalIgnoreCase))
        {
            FileStream? fs = null;
            try
            {
                fs = File.OpenRead(imagePath);
                var image = CdiParser.Parse(fs);
                var track = image.AllTracks.FirstOrDefault(t => t.Mode != CdiTrackMode.Audio);
                if (track is null)
                {
                    fs.Dispose();
                    return new OpenedImage
                    {
                        Error = "This image has no data track — an audio disc has no filesystem.",
                    };
                }

                var view = new CdiUserDataStream(fs, track);
                var img = new OpenedImage { View = view };
                img.Own(view);
                img.Own(fs);
                return img;
            }
            catch (Exception ex)
            {
                fs?.Dispose();
                return new OpenedImage { Error = "Could not parse the CDI image: " + ex.Message };
            }
        }

        // Raw bin/cue: a .cue names the data track's mode; a bare .bin/.img is
        // probed for its sector layout. Either way RawTrackReader hands back a
        // cooked user-data view the ISO reader can walk — the psxrip case.
        if (ext.Equals(".cue", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bin", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".img", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var opened = RawTrackReader.Open(imagePath);
                var img = new OpenedImage { View = opened.View };
                img.Own(opened.View);
                img.Own(opened.Base);
                return img;
            }
            catch (Exception ex)
            {
                return new OpenedImage { Error = ex.Message };
            }
        }

        return new OpenedImage
        {
            Error = $"Browsing a {ext} image isn't supported — convert it to CDI or ISO first.",
        };
    }

    /// <summary>
    /// Map an in-image path to an output path, refusing anything that climbs out
    /// of the target directory. Paths come from the image and are untrusted: a
    /// crafted one containing "../" would otherwise write wherever it liked.
    /// </summary>
    private static string? SafeTarget(string outDir, string imagePath)
    {
        var relative = imagePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var target = Path.GetFullPath(Path.Combine(outDir, relative));
        var root = Path.GetFullPath(outDir);
        return target.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? target : null;
    }

    private static Listing Empty(string error) => new()
    {
        Files = Array.Empty<FileEntry>(),
        Filesystem = "none",
        Error = error,
    };
}