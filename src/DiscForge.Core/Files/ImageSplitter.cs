// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Globalization;
using System.Security.Cryptography;
using DiscForge.Core.Util;

namespace DiscForge.Core.Files;

/// <summary>
/// Split a disc image into numbered parts (name.cdi.001, .002, …) and join
/// them back — the classic answer to FAT32 USB sticks (4 GiB − 1 max file)
/// and anything else that can't carry a whole DVD9 image in one piece.
/// Joining is plain concatenation, so parts remain recoverable with `cat` or
/// `copy /b` even without DiscForge.
///
/// Splitting also writes an SFV manifest (name.cdi.sfv) with a CRC-32 per
/// part and, in a comment, the source's byte length and SHA-256 — all
/// computed during the same single read. Join verifies each part's CRC as it
/// copies and the whole file's SHA-256 at the end, so a bit-rotted or
/// truncated part is caught by name rather than discovered as an unreadable
/// burn later.
/// </summary>
public static class ImageSplitter
{
    /// <summary>FAT32's maximum file size: 4 GiB − 1.</summary>
    public const long Fat32MaxBytes = 4_294_967_295;

    public sealed record SplitResult(IReadOnlyList<string> Parts, string ManifestPath,
                                     long TotalBytes, string Sha256);

    public sealed record JoinResult(int Parts, long TotalBytes, bool Verified, string? Warning);

    // ---- split -------------------------------------------------------------

    public static SplitResult Split(string sourcePath, long partSizeBytes,
                                    IProgress<double>? progress = null)
    {
        if (partSizeBytes < 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(partSizeBytes),
                "Part size must be at least 1 MiB.");

        var info = new FileInfo(sourcePath);
        if (!info.Exists) throw new FileNotFoundException("Image not found.", sourcePath);
        if (info.Length == 0) throw new InvalidDataException($"'{info.Name}' is empty.");
        if (info.Length <= partSizeBytes)
            throw new InvalidDataException(
                $"'{info.Name}' is {info.Length:N0} bytes, which already fits in one " +
                $"{partSizeBytes:N0}-byte part — nothing to split.");

        int partCount = (int)((info.Length + partSizeBytes - 1) / partSizeBytes);
        if (partCount > 999)
            throw new InvalidDataException(
                $"{partCount} parts won't fit the .001–.999 naming scheme; use bigger parts.");

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var parts = new List<string>(partCount);
        var partCrcs = new List<uint>(partCount);
        var buffer = new byte[1 << 20];
        long done = 0;

        using (var src = File.OpenRead(sourcePath))
        {
            for (int p = 1; p <= partCount; p++)
            {
                string partPath = $"{sourcePath}.{p:D3}";
                parts.Add(partPath);
                var crc = new Crc32();
                long remaining = Math.Min(partSizeBytes, info.Length - done);

                using var dst = File.Create(partPath);
                while (remaining > 0)
                {
                    int n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (n <= 0) throw new EndOfStreamException("Source shrank while splitting.");
                    dst.Write(buffer, 0, n);
                    crc.Update(buffer.AsSpan(0, n));
                    sha.AppendData(buffer.AsSpan(0, n));
                    remaining -= n;
                    done += n;
                    progress?.Report(done / (double)info.Length);
                }
                partCrcs.Add(crc.Value);
            }
        }

        string sha256 = System.Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();

        string manifest = sourcePath + ".sfv";
        using (var w = new StreamWriter(manifest))
        {
            w.WriteLine("; DiscForge split manifest v1");
            w.WriteLine($"; source={info.Name} bytes={info.Length} sha256={sha256} " +
                        $"part={partSizeBytes}");
            for (int i = 0; i < parts.Count; i++)
                w.WriteLine($"{Path.GetFileName(parts[i])} {partCrcs[i]:X8}");
        }

        return new SplitResult(parts, manifest, info.Length, sha256);
    }

    // ---- join --------------------------------------------------------------

    /// <summary>
    /// Join name.xxx.001, .002, … back into one file. <paramref name="firstPart"/>
    /// may be any part or the base name; parts are found by counting up from
    /// .001 until one is missing. When the manifest is present each part's
    /// CRC-32 and the final SHA-256 are verified; without it the join still
    /// works, flagged as unverified.
    /// </summary>
    public static JoinResult Join(string firstPart, string outputPath,
                                  IProgress<double>? progress = null)
    {
        string basePath = firstPart;
        if (System.Text.RegularExpressions.Regex.IsMatch(basePath, @"\.\d{3}$"))
            basePath = basePath[..^4];

        var parts = new List<string>();
        for (int p = 1; ; p++)
        {
            string candidate = $"{basePath}.{p:D3}";
            if (!File.Exists(candidate)) break;
            parts.Add(candidate);
        }
        if (parts.Count == 0)
            throw new FileNotFoundException($"No parts found for '{basePath}' (.001 missing).");

        // Manifest, if present.
        var expectedCrcs = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        long expectedBytes = -1;
        string? expectedSha = null;
        string manifestPath = basePath + ".sfv";
        string? warning = null;
        if (File.Exists(manifestPath))
        {
            foreach (var raw in File.ReadAllLines(manifestPath))
            {
                var line = raw.Trim();
                if (line.StartsWith(';'))
                {
                    foreach (var token in line.TrimStart(';').Split(' ',
                                 StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (token.StartsWith("bytes=")) expectedBytes =
                            long.Parse(token[6..], CultureInfo.InvariantCulture);
                        if (token.StartsWith("sha256=")) expectedSha = token[7..];
                    }
                    continue;
                }
                int sp = line.LastIndexOf(' ');
                if (sp > 0 && uint.TryParse(line[(sp + 1)..], NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out uint crc))
                    expectedCrcs[line[..sp].Trim()] = crc;
            }

            int expectedParts = expectedCrcs.Count;
            if (expectedParts > 0 && expectedParts != parts.Count)
                throw new InvalidDataException(
                    $"The manifest lists {expectedParts} part(s) but {parts.Count} were found — " +
                    "a part is missing or extra.");
        }
        else warning = "No .sfv manifest found — joined without verification.";

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        long total = parts.Sum(p => new FileInfo(p).Length);
        long done = 0;

        using (var dst = File.Create(outputPath))
        {
            foreach (var part in parts)
            {
                var crc = new Crc32();
                using var src = File.OpenRead(part);
                int n;
                while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    dst.Write(buffer, 0, n);
                    crc.Update(buffer.AsSpan(0, n));
                    sha.AppendData(buffer.AsSpan(0, n));
                    done += n;
                    progress?.Report(done / (double)total);
                }

                if (expectedCrcs.TryGetValue(Path.GetFileName(part), out uint want) &&
                    crc.Value != want)
                    throw new InvalidDataException(
                        $"'{Path.GetFileName(part)}' fails its CRC-32 check " +
                        $"({crc.Value:X8}, manifest says {want:X8}) — the part is corrupt.");
            }
        }

        if (expectedBytes >= 0 && expectedBytes != done)
            throw new InvalidDataException(
                $"Joined {done:N0} bytes but the manifest says {expectedBytes:N0}.");

        string gotSha = System.Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        if (expectedSha is not null && !gotSha.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The joined file's SHA-256 does not match the manifest — the result is corrupt.");

        return new JoinResult(parts.Count, done, Verified: expectedSha is not null, warning);
    }

    // ---- sizes -------------------------------------------------------------

    /// <summary>Parse "700m", "4g", "4480m", "fat32", or plain bytes.</summary>
    public static long ParsePartSize(string text)
    {
        var s = text.Trim().ToLowerInvariant();
        if (s is "fat32") return Fat32MaxBytes;
        long mult = 1;
        if (s.EndsWith('k')) { mult = 1024; s = s[..^1]; }
        else if (s.EndsWith('m')) { mult = 1024 * 1024; s = s[..^1]; }
        else if (s.EndsWith('g')) { mult = 1024L * 1024 * 1024; s = s[..^1]; }
        if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) || v <= 0)
            throw new ArgumentException(
                $"Can't read '{text}' as a size. Use bytes, or 700m / 4g, or fat32.");
        return v * mult;
    }
}
