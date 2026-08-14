// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Security.Cryptography;
using DiscForge.Core.Util;

namespace DiscForge.Core.Files;

/// <summary>
/// Image checksums: CRC-32, MD5, SHA-1 and SHA-256 computed in ONE streaming
/// pass — a 4.7 GB image is read once, not four times. CRC-32 matches zlib
/// (and DiscForge's own per-track CRCs); the rest match md5sum/sha1sum/
/// sha256sum, so sidecars interoperate with everyday tooling.
///
/// MD5 and SHA-1 are here for identity and interchange (that's what the wider
/// imaging world publishes), not for security — which is why their presence
/// alongside SHA-256 is a feature, not an oversight.
/// </summary>
public static class ImageChecksums
{
    public sealed record ChecksumSet(long Length, string Crc32, string Md5, string Sha1, string Sha256)
    {
        /// <summary>md5sum-style line: "&lt;hex&gt;  &lt;name&gt;".</summary>
        public string Line(string algorithm, string fileName) => algorithm.ToLowerInvariant() switch
        {
            "crc32" => $"{fileName} {Crc32.ToUpperInvariant()}",       // SFV style
            "md5" => $"{Md5}  {fileName}",
            "sha1" => $"{Sha1}  {fileName}",
            "sha256" => $"{Sha256}  {fileName}",
            _ => throw new ArgumentException($"Unknown algorithm '{algorithm}'."),
        };
    }

    /// <summary>Compute all four digests over a stream in one pass.</summary>
    public static ChecksumSet Compute(Stream source, IProgress<double>? progress = null)
    {
        // These algorithms identify files; they are not used for security here.
#pragma warning disable CA5350, CA5351
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
#pragma warning restore CA5350, CA5351
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var crc = new Crc32();

        long total = source.CanSeek ? source.Length - source.Position : -1;
        long done = 0;
        var buffer = new byte[1 << 20];
        int n;
        while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            var span = buffer.AsSpan(0, n);
            crc.Update(span);
            md5.AppendData(span);
            sha1.AppendData(span);
            sha256.AppendData(span);
            done += n;
            if (total > 0) progress?.Report(done / (double)total);
        }

        return new ChecksumSet(
            done,
            crc.Value.ToString("x8"),
            System.Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant(),
            System.Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant(),
            System.Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant());
    }

    public static ChecksumSet ComputeFile(string path, IProgress<double>? progress = null)
    {
        using var fs = File.OpenRead(path);
        return Compute(fs, progress);
    }

    // ---- sidecars ----------------------------------------------------------

    /// <summary>A parsed checksum sidecar: which digest, the expected hex.</summary>
    public sealed record Sidecar(string Algorithm, string ExpectedHex, string SidecarPath);

    /// <summary>Sidecar extensions DiscForge writes and verifies, strongest first.</summary>
    private static readonly (string ext, string algo)[] Kinds =
    {
        (".sha256", "sha256"), (".sha1", "sha1"), (".md5", "md5"), (".sfv", "crc32"),
    };

    /// <summary>Write a sidecar next to the file. Returns the sidecar path.</summary>
    public static string WriteSidecar(string imagePath, ChecksumSet sums, string algorithm)
    {
        string ext = Kinds.FirstOrDefault(k => k.algo == algorithm.ToLowerInvariant()).ext
            ?? throw new ArgumentException($"Unknown algorithm '{algorithm}'.");
        string sidecar = imagePath + ext;
        string name = Path.GetFileName(imagePath);
        string content = algorithm.Equals("crc32", StringComparison.OrdinalIgnoreCase)
            ? $"; DiscForge\n{sums.Line("crc32", name)}\n"
            : sums.Line(algorithm, name) + "\n";
        File.WriteAllText(sidecar, content);
        return sidecar;
    }

    /// <summary>
    /// Find the strongest sidecar next to an image and read the expected value
    /// for it. Returns null when none exists. Understands md5sum-style lines
    /// ("hex  name", '*' binary markers included) and SFV ("name HEX").
    /// </summary>
    public static Sidecar? FindSidecar(string imagePath)
    {
        string name = Path.GetFileName(imagePath);
        foreach (var (ext, algo) in Kinds)
        {
            string candidate = imagePath + ext;
            if (!File.Exists(candidate)) continue;

            foreach (var raw in File.ReadAllLines(candidate))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;

                if (algo == "crc32")
                {
                    // SFV: "<name> <HEX8>" — name may contain spaces; hex is last.
                    int sp = line.LastIndexOf(' ');
                    if (sp <= 0) continue;
                    string fn = line[..sp].Trim();
                    string hex = line[(sp + 1)..].Trim();
                    if (hex.Length == 8 &&
                        (fn.Equals(name, StringComparison.OrdinalIgnoreCase) || CountDataLines(candidate) == 1))
                        return new Sidecar(algo, hex.ToLowerInvariant(), candidate);
                }
                else
                {
                    // md5sum family: "<hex>  <name>" or "<hex> *<name>".
                    int sp = line.IndexOf(' ');
                    if (sp <= 0) continue;
                    string hex = line[..sp].Trim();
                    string fn = line[(sp + 1)..].Trim().TrimStart('*').Trim();
                    if (IsHex(hex) &&
                        (fn.Equals(name, StringComparison.OrdinalIgnoreCase) || CountDataLines(candidate) == 1))
                        return new Sidecar(algo, hex.ToLowerInvariant(), candidate);
                }
            }
        }
        return null;
    }

    /// <summary>The computed value for a sidecar's algorithm.</summary>
    public static string ValueFor(ChecksumSet sums, string algorithm) => algorithm switch
    {
        "crc32" => sums.Crc32,
        "md5" => sums.Md5,
        "sha1" => sums.Sha1,
        "sha256" => sums.Sha256,
        _ => throw new ArgumentException($"Unknown algorithm '{algorithm}'."),
    };

    private static bool IsHex(string s) =>
        s.Length is 32 or 40 or 64 && s.All(Uri.IsHexDigit);

    private static int CountDataLines(string path) =>
        File.ReadAllLines(path).Count(l =>
        {
            var t = l.Trim();
            return t.Length > 0 && !t.StartsWith(';') && !t.StartsWith('#');
        });
}
