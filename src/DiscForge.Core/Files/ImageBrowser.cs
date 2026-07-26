// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Cdi;
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