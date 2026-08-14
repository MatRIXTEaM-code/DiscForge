// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Xbox;

/// <summary>What kind of STFS package a GOD header file is.</summary>
public enum GodPackageKind { Con, Live, Pirs, Unknown }

/// <summary>One <c>Data####</c> payload file that makes up a GOD package's body.</summary>
public sealed record GodDataFile
{
    public required string Path { get; init; }
    public required long Size { get; init; }
}

/// <summary>Identification of an Xbox 360 "Games on Demand" (GOD) / STFS package.</summary>
public sealed record GodInfo
{
    public required GodPackageKind Kind { get; init; }
    /// <summary>The STFS content-type word (0x7000 = Games on Demand).</summary>
    public required uint ContentType { get; init; }
    /// <summary>Declared content size in bytes, from the header metadata.</summary>
    public required long ContentSize { get; init; }
    /// <summary>The <c>Data####</c> files found next to the header, in order.</summary>
    public required IReadOnlyList<GodDataFile> DataFiles { get; init; }
    /// <summary>Combined size of every <c>Data####</c> file on disk.</summary>
    public long DataFilesTotal => DataFiles.Sum(f => f.Size);
    public bool LooksLikeGamesOnDemand => ContentType == GamesOnDemandType;

    public const uint GamesOnDemandType = 0x7000;
}

/// <summary>
/// Reads the header/metadata of an Xbox 360 GOD (Games on Demand) package for
/// <b>identification</b> — the package kind (CON/LIVE/PIRS), the content-type word,
/// the declared content size, and the inventory of <c>Data####</c> payload files
/// that hold the disc image. This is pure structure parsing: it decrypts nothing and
/// never validates or forges the package's RSA signature, so it stays inside the
/// clean-room boundary (identify/preserve, never circumvent).
///
/// This type IDENTIFIES only. Full GOD → ISO reconstruction lives in <see cref="GodExtractor"/>,
/// which resolves the one-block block→offset ambiguity (free60 vs py360) SAFELY: it reconstructs with
/// both conventions and accepts a result only when it is a valid XDVDFS volume, declining otherwise —
/// so a wrong formula can never write a corrupt ISO. See docs/XBOX.md.
///
/// Header layout used (big-endian; offsets per the STFS metadata):
///   0x000  magic: "CON ", "LIVE" or "PIRS"
///   0x344  content type (u32)   — 0x7000 marks Games on Demand
///   0x34C  content size (u64)
/// </summary>
public static class GodContainer
{
    /// <summary>Sniff whether a file begins with an STFS magic.</summary>
    public static bool IsStfsHeader(ReadOnlySpan<byte> head)
        => head.Length >= 4 && MagicOf(head) != GodPackageKind.Unknown;

    private static GodPackageKind MagicOf(ReadOnlySpan<byte> head) =>
        head[0] == 'C' && head[1] == 'O' && head[2] == 'N' && head[3] == ' ' ? GodPackageKind.Con :
        head[0] == 'L' && head[1] == 'I' && head[2] == 'V' && head[3] == 'E' ? GodPackageKind.Live :
        head[0] == 'P' && head[1] == 'I' && head[2] == 'R' && head[3] == 'S' ? GodPackageKind.Pirs :
        GodPackageKind.Unknown;

    /// <summary>
    /// Read a GOD header file and inventory its payload. The <c>Data####</c> files are
    /// expected in a sibling directory named <c>&lt;header&gt;.data</c> (the layout
    /// iso2god and the console use); if that directory is absent the data list is empty.
    /// </summary>
    public static GodInfo Read(string headerPath)
    {
        ArgumentNullException.ThrowIfNull(headerPath);
        if (!File.Exists(headerPath)) throw new FileNotFoundException("GOD header not found.", headerPath);

        byte[] head = ReadHead(headerPath, 0x360);
        var kind = MagicOf(head);
        if (kind == GodPackageKind.Unknown)
            throw new InvalidDataException(
                "Not an STFS/GOD package: the file does not begin with \"CON \", \"LIVE\" or \"PIRS\".");

        uint contentType = head.Length >= 0x348 ? BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0x344)) : 0;
        long contentSize = head.Length >= 0x354 ? (long)BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(0x34C)) : 0;

        return new GodInfo
        {
            Kind = kind,
            ContentType = contentType,
            ContentSize = contentSize,
            DataFiles = FindDataFiles(headerPath),
        };
    }

    // GOD payloads live in "<header>.data/Data0000, Data0001, …" next to the header.
    private static IReadOnlyList<GodDataFile> FindDataFiles(string headerPath)
    {
        string dir = headerPath + ".data";
        if (!Directory.Exists(dir))
        {
            // Some layouts drop the Data#### files directly beside the header.
            dir = Path.GetDirectoryName(Path.GetFullPath(headerPath)) ?? ".";
        }

        var files = new List<GodDataFile>();
        foreach (var path in Directory.EnumerateFiles(dir))
        {
            string name = Path.GetFileName(path);
            if (name.Length == 8 && name.StartsWith("Data", StringComparison.OrdinalIgnoreCase)
                && name.AsSpan(4).ToArray().All(char.IsDigit))
            {
                files.Add(new GodDataFile { Path = path, Size = new FileInfo(path).Length });
            }
        }
        files.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(a.Path), Path.GetFileName(b.Path)));
        return files;
    }

    private static byte[] ReadHead(string path, int count)
    {
        using var fs = File.OpenRead(path);
        int n = (int)Math.Min(count, fs.Length);
        var buf = new byte[n];
        fs.ReadExactly(buf, 0, n);
        return buf;
    }

    /// <summary>A short human-readable description of the package kind.</summary>
    public static string Describe(GodPackageKind kind) => kind switch
    {
        GodPackageKind.Con => "CON (console-signed)",
        GodPackageKind.Live => "LIVE (Xbox Live-signed)",
        GodPackageKind.Pirs => "PIRS (Microsoft-signed)",
        _ => "unknown",
    };
}
