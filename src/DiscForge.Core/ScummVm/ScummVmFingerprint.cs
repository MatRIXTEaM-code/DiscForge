// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Security.Cryptography;

namespace DiscForge.Core.ScummVm;

/// <summary>
/// Computes the fingerprint ScummVM's <i>Advanced Detector</i> uses to identify a
/// game: for each data file, the MD5 of its first N bytes (ScummVM's default is
/// 5000, engine-overridable) together with the file's exact byte size. ScummVM
/// matches that (size, head-MD5) pair against its built-in detection tables, so a
/// disc DiscForge has imaged and extracted can be checked against — or contributed
/// to — the ScummVM database.
///
/// This is a <i>detection</i> aid, a sibling of DiscForge's format and protection
/// identification: it emits the exact bytes ScummVM hashes, so the user can look a
/// title up. It does not bundle ScummVM's tables and names nothing by itself.
///
/// Clean-room: this only hashes the user's own extracted files. Nothing here is
/// protection-related.
/// </summary>
public static class ScummVmFingerprint
{
    /// <summary>ScummVM's default number of leading bytes hashed (kMD5FileSizeLimit).</summary>
    public const int DefaultBytes = 5000;

    /// <summary>One file's ScummVM fingerprint. <see cref="Name"/> is the file's
    /// name (or a path relative to the scanned directory, using '/').</summary>
    public sealed record Fingerprint(string Name, long Size, string Md5);

    /// <summary>MD5 (lowercase hex) of the first <paramref name="limit"/> bytes of a
    /// stream — or the whole stream when it is shorter, exactly as ScummVM does.</summary>
    public static string HeadMd5(Stream source, int limit = DefaultBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit), "Byte limit must be positive.");

        var buffer = new byte[limit];
        int filled = 0;
        int n;
        while (filled < limit && (n = source.Read(buffer, filled, limit - filled)) > 0)
            filled += n;

        // MD5 here identifies a file for ScummVM lookup; it is not used for security.
#pragma warning disable CA5351
        byte[] hash = MD5.HashData(buffer.AsSpan(0, filled));
#pragma warning restore CA5351
        return System.Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Fingerprint a single file.</summary>
    public static Fingerprint ForFile(string path, int limit = DefaultBytes)
    {
        ArgumentNullException.ThrowIfNull(path);
        long size = new FileInfo(path).Length;
        using var fs = File.OpenRead(path);
        return new Fingerprint(Path.GetFileName(path), size, HeadMd5(fs, limit));
    }

    /// <summary>
    /// Fingerprint every file in a directory (the natural input — a ScummVM game
    /// folder). Names are relative to <paramref name="directory"/> with '/'
    /// separators, and the result is sorted by name for a stable, diffable listing.
    /// </summary>
    public static IReadOnlyList<Fingerprint> ForDirectory(string directory, bool recursive = false, int limit = DefaultBytes)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"'{directory}' is not a directory.");

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var results = new List<Fingerprint>();
        foreach (var path in Directory.EnumerateFiles(directory, "*", option))
        {
            string rel = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
            long size = new FileInfo(path).Length;
            using var fs = File.OpenRead(path);
            results.Add(new Fingerprint(rel, size, HeadMd5(fs, limit)));
        }
        results.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return results;
    }
}
