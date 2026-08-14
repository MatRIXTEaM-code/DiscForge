// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Security.Cryptography;
using DiscForge.Core.Dat;
using DiscForge.Core.Identify;
using DiscForge.Core.Rom;

namespace DiscForge.Core.Library;

/// <summary>Where a scanned file stands relative to a loaded DAT.</summary>
public enum LibraryStatus
{
    /// <summary>Hash matches a catalogued dump — a confirmed-good file.</summary>
    Verified,
    /// <summary>Verified good, but its filename is not the DAT's canonical name.</summary>
    Misnamed,
    /// <summary>A byte-identical copy of another scanned, verified file.</summary>
    Duplicate,
    /// <summary>Recognised as some format, but not found in the DAT.</summary>
    Unknown,
    /// <summary>No DAT was supplied, so only identification/hashing was done.</summary>
    Unchecked,
}

/// <summary>One file the scanner examined.</summary>
public sealed record LibraryEntry
{
    public required string Path { get; init; }
    public required string FileName { get; init; }
    public required long Size { get; init; }
    /// <summary>The universal identifier's verdict (e.g. "SNES", "CHD", "ISO 9660").</summary>
    public required string Format { get; init; }
    public required uint Crc32 { get; init; }
    public required string Md5 { get; init; }
    public required string Sha1 { get; init; }
    /// <summary>When the file is a cartridge ROM, the detected platform; else empty.</summary>
    public string RomPlatform { get; init; } = "";
    public required LibraryStatus Status { get; init; }
    /// <summary>The DAT entry this file matches, when verified.</summary>
    public DatRom? Match { get; init; }
    /// <summary>The canonical filename the DAT gives, when it differs from the current name.</summary>
    public string? SuggestedName { get; init; }

    public string Crc32Hex => Crc32.ToString("x8");
}

/// <summary>One planned rename: move <see cref="From"/> to <see cref="To"/> (same folder).</summary>
public sealed record RenamePlanItem(string From, string To);

/// <summary>The result of scanning a folder tree.</summary>
public sealed record LibraryReport
{
    public required string Root { get; init; }
    public required IReadOnlyList<LibraryEntry> Entries { get; init; }
    /// <summary>DAT entries not present among the scanned files (gaps in the set).
    /// Empty when no DAT was supplied.</summary>
    public required IReadOnlyList<DatRom> Missing { get; init; }
    public string? DatName { get; init; }

    public int Total => Entries.Count;
    public int Verified => Entries.Count(e => e.Status is LibraryStatus.Verified or LibraryStatus.Misnamed);
    public int Misnamed => Entries.Count(e => e.Status == LibraryStatus.Misnamed);
    public int Duplicates => Entries.Count(e => e.Status == LibraryStatus.Duplicate);
    public int Unknown => Entries.Count(e => e.Status == LibraryStatus.Unknown);

    /// <summary>The canonical-rename plan: every verified-but-misnamed file.</summary>
    public IReadOnlyList<RenamePlanItem> RenamePlan() =>
        Entries.Where(e => e.Status == LibraryStatus.Misnamed && e.SuggestedName is not null)
               .Select(e => new RenamePlanItem(e.Path,
                    System.IO.Path.Combine(System.IO.Path.GetDirectoryName(e.Path) ?? ".", e.SuggestedName!)))
               .ToList();
}

/// <summary>
/// The collection / library manager: point it at a folder tree and it identifies,
/// hashes and (against a Redump/No-Intro DAT) verifies every file, then reports what is
/// confirmed-good, mis-named, duplicated, unrecognised, or missing from the set, and
/// plans the canonical renames. It composes the pieces DiscForge already has — the
/// universal <see cref="FormatIdentifier"/>, the ROM header/hash rules
/// (<see cref="RomHashes"/>, which applies the No-Intro header conventions), and the
/// <see cref="DatFile"/> verifier — into one batch workflow, so a whole collection can
/// be checked and tidied in a single pass. Nothing here is protection-related.
/// </summary>
public static class LibraryScanner
{
    // Read a file wholly into memory only up to this size (cartridge ROMs, saves,
    // audio). Larger files (disc images) are hashed by streaming.
    private const long MaxInMemoryBytes = 96L * 1024 * 1024;

    /// <summary>
    /// Scan <paramref name="root"/> (recursively). If <paramref name="dat"/> is supplied
    /// each file is verified against it. <paramref name="progress"/>, when given, is
    /// called with each file path as it is processed.
    /// </summary>
    public static LibraryReport Scan(string root, DatFile? dat = null, Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Folder not found: {root}");

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                             .ToList();

        // First pass: identify + hash every file.
        var raw = new List<(string Path, long Size, string Format, uint Crc, string Md5, string Sha1, string Platform)>();
        foreach (var path in files)
        {
            progress?.Invoke(path);
            try
            {
                var info = new FileInfo(path);
                string format; uint crc; string md5, sha1, platform = "";
                using (var fs = File.OpenRead(path))
                    format = FormatIdentifier.Identify(fs).Name;

                if (info.Length <= MaxInMemoryBytes)
                {
                    byte[] data = File.ReadAllBytes(path);
                    var rid = RomIdentify.Identify(data);
                    if (rid.Platform is not ("Unknown" or ""))
                    {
                        // Hash the ROM the No-Intro way (header rules applied).
                        var h = RomHashes.Compute(data, rid);
                        (crc, md5, sha1) = (h.Crc32, h.Md5.ToLowerInvariant(), h.Sha1.ToLowerInvariant());
                        platform = rid.Platform;
                    }
                    else
                    {
                        (crc, md5, sha1) = HashBytes(data);
                    }
                }
                else
                {
                    using var fs = File.OpenRead(path);
                    (crc, md5, sha1) = HashStream(fs);
                }
                raw.Add((path, info.Length, format, crc, md5, sha1, platform));
            }
            catch (Exception)
            {
                // A file we cannot read at all is recorded as unknown with no hash.
                raw.Add((path, SafeLen(path), "unreadable", 0, "", "", ""));
            }
        }

        // Second pass: DAT match, duplicate detection, naming.
        var bySha1 = raw.Where(r => r.Sha1.Length > 0)
                        .GroupBy(r => r.Sha1, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var seenSha1 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedDatKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var entries = new List<LibraryEntry>(raw.Count);
        foreach (var r in raw)
        {
            DatMatch? match = dat?.Verify(r.Size, r.Crc.ToString("x8"), r.Sha1.Length > 0 ? r.Sha1 : null,
                                          r.Md5.Length > 0 ? r.Md5 : null);
            string fileName = System.IO.Path.GetFileName(r.Path);

            LibraryStatus status;
            string? suggested = null;
            if (match is { Verified: true, Rom: { } rom })
            {
                if (rom.Sha1 is not null) matchedDatKeys.Add(rom.Sha1);
                else if (rom.Crc is not null) matchedDatKeys.Add("crc:" + rom.Crc);

                // A byte-identical file already seen (same SHA-1) is a duplicate.
                bool isDup = r.Sha1.Length > 0 && !seenSha1.Add(r.Sha1);
                if (isDup) status = LibraryStatus.Duplicate;
                else if (!string.Equals(fileName, rom.Name, StringComparison.Ordinal))
                {
                    status = LibraryStatus.Misnamed;
                    suggested = rom.Name;
                }
                else status = LibraryStatus.Verified;
            }
            else if (dat is null) status = LibraryStatus.Unchecked;
            else status = LibraryStatus.Unknown;

            entries.Add(new LibraryEntry
            {
                Path = r.Path, FileName = fileName, Size = r.Size, Format = r.Format,
                Crc32 = r.Crc, Md5 = r.Md5, Sha1 = r.Sha1, RomPlatform = r.Platform,
                Status = status, Match = match?.Rom, SuggestedName = suggested,
            });
        }

        // Missing: DAT entries no scanned file matched.
        var missing = new List<DatRom>();
        if (dat is not null)
            foreach (var rom in dat.Roms)
            {
                bool present = (rom.Sha1 is not null && matchedDatKeys.Contains(rom.Sha1))
                            || (rom.Crc is not null && matchedDatKeys.Contains("crc:" + rom.Crc));
                if (!present) missing.Add(rom);
            }

        return new LibraryReport
        {
            Root = root, Entries = entries, Missing = missing, DatName = dat?.Name,
        };
    }

    /// <summary>
    /// Apply a rename plan on disk. Returns the count actually renamed. A target that
    /// already exists (and is a different file) is skipped rather than overwritten;
    /// case-only renames are handled via a temporary name. Missing sources are skipped.
    /// </summary>
    public static int ApplyRenames(IReadOnlyList<RenamePlanItem> plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        int done = 0;
        foreach (var item in plan)
        {
            if (!File.Exists(item.From)) continue;
            if (string.Equals(item.From, item.To, StringComparison.Ordinal)) continue;

            bool caseOnly = string.Equals(item.From, item.To, StringComparison.OrdinalIgnoreCase);
            if (File.Exists(item.To) && !caseOnly) continue;   // don't clobber a different file

            if (caseOnly)
            {
                string tmp = item.To + ".dfrename.tmp";
                File.Move(item.From, tmp);
                File.Move(tmp, item.To);
            }
            else
            {
                File.Move(item.From, item.To);
            }
            done++;
        }
        return done;
    }

    // ---- hashing (single pass: CRC-32 + MD5 + SHA-1) ------------------------

    private static (uint Crc, string Md5, string Sha1) HashBytes(byte[] data)
    {
        uint crc = Crc32(data);
        string md5 = Hex(MD5.HashData(data));
        string sha1 = Hex(SHA1.HashData(data));
        return (crc, md5, sha1);
    }

    private static (uint Crc, string Md5, string Sha1) HashStream(Stream s)
    {
        uint crc = 0xFFFFFFFF;
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buf = new byte[1 << 20];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < n; i++) crc = CrcTable[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
            md5.AppendData(buf, 0, n);
            sha1.AppendData(buf, 0, n);
        }
        crc ^= 0xFFFFFFFF;
        return (crc, Hex(md5.GetHashAndReset()), Hex(sha1.GetHashAndReset()));
    }

    private static long SafeLen(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static string Hex(byte[] b) => System.Convert.ToHexString(b).ToLowerInvariant();

    // Standard CRC-32 (zlib/PNG polynomial), the checksum Redump/No-Intro DATs key on.
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
