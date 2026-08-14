// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Iso;

/// <summary>The outcome of building an ISO 9660 image from a folder.</summary>
public sealed record IsoFromFolderResult
{
    public required string VolumeId { get; init; }
    public required int Files { get; init; }
    public required int Directories { get; init; }
    public required long ImageBytes { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    /// <summary>The El Torito boot image the disc was made bootable from, or null for a plain data disc.</summary>
    public string? BootImage { get; init; }
    /// <summary>The UEFI (EFI-platform) boot image, when the disc was made BIOS+UEFI bootable, else null.</summary>
    public string? EfiBootImage { get; init; }
}

/// <summary>
/// iso-create — build a standard ISO 9660 image from a folder on disk. DiscForge could already build an Xbox
/// XISO and read every optical filesystem, but had no general "make a data-disc image from these files" builder
/// exposed. This walks a directory tree, lays it out as ISO 9660 (with Joliet for long/Unicode names by default,
/// optional Rock Ridge for POSIX names), optionally makes it bootable from a caller-supplied El Torito boot image,
/// and streams the image out with constant memory regardless of size.
/// It authors a filesystem image from files the person already has; it decrypts and defeats nothing, and it embeds
/// only the boot loader the caller hands it — never anything copyrighted of its own.
/// </summary>
public static class IsoFromFolder
{
    /// <summary>Build the ISO for <paramref name="folder"/> and stream it to <paramref name="output"/>.</summary>
    /// <param name="bootImagePath">A boot loader binary to make the disc bootable via El Torito, or null for a plain data disc.</param>
    /// <param name="bootMedia">El Torito emulation type; no-emulation (the default) is right for modern loaders like isolinux/GRUB.</param>
    public static IsoFromFolderResult Write(string folder, string? volumeId, Stream output,
                                            bool joliet = true, bool rockRidge = false,
                                            string? bootImagePath = null,
                                            IsoBuilder.BootMediaType bootMedia = IsoBuilder.BootMediaType.NoEmulation,
                                            string? efiBootImagePath = null)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(output);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"'{folder}' is not a folder.");

        var boot = LoadBoot(bootImagePath, bootMedia);
        // A UEFI boot image is loaded as no-emulation (the only mode UEFI firmware honours).
        var efiBoot = LoadBoot(efiBootImagePath, IsoBuilder.BootMediaType.NoEmulation);

        string vol = NormaliseVolumeId(volumeId ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))));
        int files = 0, dirs = 0;
        var roots = Walk(folder, ref files, ref dirs);
        if (files == 0 && dirs == 0)
            throw new InvalidOperationException($"'{folder}' is empty — nothing to put in the image.");

        var layout = IsoBuilder.Plan(vol, roots, joliet, boot, rockRidge, efiBoot: efiBoot);
        layout.WriteTo(output);

        return new IsoFromFolderResult
        {
            VolumeId = vol, Files = files, Directories = dirs,
            ImageBytes = layout.ImageBytes, Warnings = layout.Warnings,
            BootImage = bootImagePath is null ? null : Path.GetFileName(bootImagePath),
            EfiBootImage = efiBootImagePath is null ? null : Path.GetFileName(efiBootImagePath),
        };
    }

    private static IsoBuilder.BootImage? LoadBoot(string? path, IsoBuilder.BootMediaType media)
    {
        if (path is null) return null;
        if (!File.Exists(path)) throw new FileNotFoundException($"boot image '{path}' not found.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0) throw new InvalidOperationException($"boot image '{path}' is empty.");
        return new IsoBuilder.BootImage(bytes, media);
    }

    private static List<IsoBuilder.Node> Walk(string dir, ref int files, ref int dirs)
    {
        var nodes = new List<IsoBuilder.Node>();
        foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(p => p, StringComparer.Ordinal))
        {
            dirs++;
            var children = Walk(sub, ref files, ref dirs);
            nodes.Add(IsoBuilder.Node.Dir(Path.GetFileName(sub), children));
        }
        foreach (var f in Directory.EnumerateFiles(dir).OrderBy(p => p, StringComparer.Ordinal))
        {
            files++;
            nodes.Add(IsoBuilder.Node.FromPath(f));
        }
        return nodes;
    }

    /// <summary>ISO 9660 volume identifiers are up to 32 d-characters; keep it sane and non-empty.</summary>
    private static string NormaliseVolumeId(string raw)
    {
        var chars = raw.ToUpperInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_')
            .Take(32)
            .ToArray();
        var s = new string(chars).Trim('_');
        return s.Length == 0 ? "DISC" : s;
    }
}
