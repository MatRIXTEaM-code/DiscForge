// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Ciso;
using DiscForge.Core.Iso;

namespace DiscForge.Core.Psp;

/// <summary>Raised when an image does not look like a PSP UMD.</summary>
public sealed class PspFormatException(string message) : Exception(message);

/// <summary>
/// A PSP UMD's descriptive metadata plus its filesystem. Everything here is read
/// from the plain ISO 9660 filesystem and the PARAM.SFO table Sony ships in the
/// clear — no UMD protection or decryption is touched.
/// </summary>
public sealed record PspGame
{
    /// <summary>The disc id, e.g. "ULUS12345" (SFO key DISC_ID).</summary>
    public required string DiscId { get; init; }
    /// <summary>The human title (SFO key TITLE).</summary>
    public required string Title { get; init; }
    /// <summary>The category, e.g. "UG" for a UMD game (SFO key CATEGORY).</summary>
    public required string Category { get; init; }
    /// <summary>The disc version string, e.g. "1.00" (SFO key DISC_VERSION).</summary>
    public required string DiscVersion { get; init; }
    /// <summary>The region, derived from the DISC_ID prefix (UL US/EU/JP…), or "".</summary>
    public required string Region { get; init; }
    /// <summary>The whole UMD filesystem, as read by <see cref="IsoReader"/>.</summary>
    public required IsoDirectory Filesystem { get; init; }
    /// <summary>The parsed PARAM.SFO the metadata came from.</summary>
    public required ParamSfo Sfo { get; init; }
}

/// <summary>
/// Reads a PSP UMD disc image — a plain ISO 9660 image, or a CSO/ZSO
/// block-compressed one — and surfaces its game metadata and filesystem.
///
/// A UMD's data area is an ordinary ISO 9660 volume; the game lives under
/// PSP_GAME/, and PSP_GAME/PARAM.SFO carries the descriptive metadata. The reader
/// decompresses CSO/ZSO to a plain ISO first when needed, reads the filesystem
/// with <see cref="IsoReader"/>, locates PARAM.SFO case-insensitively and parses
/// it with <see cref="ParamSfo"/>.
/// </summary>
public static class PspUmdReader
{
    /// <summary>Read a PSP UMD from a file path (.iso, .cso or .zso).</summary>
    public static PspGame Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var fs = File.OpenRead(path);
        return Read(fs);
    }

    /// <summary>Read a PSP UMD from a seekable stream.</summary>
    public static PspGame Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
            throw new ArgumentException("Reading a PSP UMD requires a seekable stream.", nameof(stream));

        var iso = AsIso(stream);
        var fs = IsoReader.Read(iso);

        var sfoEntry = FindParamSfo(fs);
        if (sfoEntry is null)
            throw new PspFormatException(
                "No PSP_GAME/PARAM.SFO found — this image does not look like a PSP UMD.");

        using var sfoBytes = new MemoryStream();
        IsoReader.ExtractFile(iso, sfoEntry, sfoBytes);
        var sfo = ParamSfo.Parse(sfoBytes.ToArray());

        string discId = sfo.GetString("DISC_ID");
        return new PspGame
        {
            DiscId = discId,
            Title = sfo.GetString("TITLE"),
            Category = sfo.GetString("CATEGORY"),
            DiscVersion = sfo.GetString("DISC_VERSION"),
            Region = RegionFromDiscId(discId),
            Filesystem = fs,
            Sfo = sfo,
        };
    }

    /// <summary>True when the image carries a PSP_GAME/PARAM.SFO (i.e. looks like a
    /// PSP UMD). Leaves the stream position undefined.</summary>
    public static bool IsPspUmd(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek) return false;
        try
        {
            var iso = AsIso(stream);
            var fs = IsoReader.Read(iso);
            return FindParamSfo(fs) is not null;
        }
        catch (Exception ex) when (ex is IsoFormatException or CisoFormatException or ParamSfoFormatException)
        {
            return false;
        }
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>Present the input as a plain ISO stream, decompressing CSO/ZSO when
    /// the leading bytes carry the CISO/ZISO signature.</summary>
    private static Stream AsIso(Stream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var head = new byte[4];
        int n = stream.Read(head, 0, 4);
        stream.Seek(0, SeekOrigin.Begin);

        if (n == 4 && CisoImage.IsCiso(head))
        {
            var iso = new MemoryStream();
            CisoImage.Decompress(stream, iso);
            iso.Seek(0, SeekOrigin.Begin);
            return iso;
        }
        return stream;
    }

    /// <summary>Locate PSP_GAME/PARAM.SFO case-insensitively. Accepts it directly
    /// under PSP_GAME (the norm) or, failing that, any PARAM.SFO on the disc.</summary>
    private static IsoEntry? FindParamSfo(IsoDirectory fs)
    {
        IsoEntry? Match(Func<IsoEntry, bool> pred)
            => fs.Files.FirstOrDefault(pred);

        return Match(e => e.Path.Equals("/PSP_GAME/PARAM.SFO", StringComparison.OrdinalIgnoreCase))
            ?? Match(e => e.Path.EndsWith("/PSP_GAME/PARAM.SFO", StringComparison.OrdinalIgnoreCase))
            ?? Match(e => e.Name.Equals("PARAM.SFO", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Best-effort region from the DISC_ID prefix. UL/UC + region letter:
    /// U=Americas, E=Europe, J/H=Japan/Asia, K=Korea.</summary>
    private static string RegionFromDiscId(string discId)
    {
        if (discId.Length < 3) return "";
        return discId[2] switch
        {
            'U' => "Americas",
            'E' => "Europe",
            'J' or 'H' => "Japan/Asia",
            'K' => "Korea",
            _ => "",
        };
    }
}
