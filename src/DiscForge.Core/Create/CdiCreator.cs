// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cdi;
using DiscForge.Core.Iso;

namespace DiscForge.Core.Create;

/// <summary>
/// End-to-end "create image" path: a set of files -> ISO 9660 (via IsoBuilder)
/// -> single-track data CDI (via CdiWriter). This is the creation half of a
/// modern DiscJuggler, fully self-contained — no mkisofs, no external tools.
/// </summary>
public static class CdiCreator
{
    public sealed record CreateResult(long CdiBytes, int IsoSectors, IReadOnlyList<string> Warnings);

    /// <summary>
    /// Build a data CDI from files. The ISO is stored as a Mode 1 / 2048 single
    /// data track — the standard cooked-data layout that IMAPI2 can burn on any
    /// modern drive (the burn planner routes it accordingly).
    /// </summary>
    public static CreateResult CreateDataImage(
        string volumeId, IReadOnlyList<IsoBuilder.FileEntry> files,
        CdiVersion version, Stream output, bool rockRidge = false)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(output);
        var nodes = files.Select(f => IsoBuilder.Node.File(f.Name, f.Data)).ToList();
        var layout = IsoBuilder.Plan(volumeId, nodes, joliet: true, boot: null, rockRidge: rockRidge);
        return WriteIsoStreaming(volumeId, layout, version, output);
    }

    /// <summary>Convenience: read a directory tree (recursively) and create a CDI.
    /// Set <paramref name="rockRidge"/> for POSIX long names (Linux/macOS) in
    /// addition to Joliet (Windows) — a cross-platform disc.</summary>
    public static CreateResult CreateFromDirectory(
        string volumeId, string directory, CdiVersion version, Stream output, bool rockRidge = false)
    {
        var rootChildren = ReadDirectory(directory);
        return CreateDataImageTree(volumeId, rootChildren, version, output, rockRidge);
    }

    /// <summary>Create a data CDI from an explicit directory tree.</summary>
    public static CreateResult CreateDataImageTree(
        string volumeId, IReadOnlyList<IsoBuilder.Node> rootChildren,
        CdiVersion version, Stream output, bool rockRidge = false)
    {
        var layout = IsoBuilder.Plan(volumeId, rootChildren, joliet: true, boot: null, rockRidge: rockRidge);
        return WriteIsoStreaming(volumeId, layout, version, output);
    }

    /// <summary>
    /// Create a bootable (El Torito) data CDI from a directory tree plus a
    /// caller-supplied boot image. DiscForge embeds no boot code of its own —
    /// the boot image is whatever the caller provides.
    /// </summary>
    public static CreateResult CreateBootableImage(
        string volumeId, IReadOnlyList<IsoBuilder.Node> rootChildren,
        IsoBuilder.BootImage boot, CdiVersion version, Stream output, bool rockRidge = false)
    {
        var layout = IsoBuilder.Plan(volumeId, rootChildren, joliet: true, boot: boot, rockRidge: rockRidge);
        return WriteIsoStreaming(volumeId, layout, version, output);
    }

    /// <summary>Convenience: read a directory tree and create a bootable (El
    /// Torito) CDI with a caller-supplied boot image.</summary>
    public static CreateResult CreateBootableFromDirectory(
        string volumeId, string directory, IsoBuilder.BootImage boot,
        CdiVersion version, Stream output, bool rockRidge = false)
    {
        var rootChildren = ReadDirectory(directory);
        return CreateBootableImage(volumeId, rootChildren, boot, version, output, rockRidge);
    }

    private static List<IsoBuilder.Node> ReadDirectory(string directory)
    {
        var nodes = new List<IsoBuilder.Node>();
        foreach (var sub in Directory.EnumerateDirectories(directory))
            nodes.Add(IsoBuilder.Node.Dir(Path.GetFileName(sub), ReadDirectory(sub)));
        // Reference files by path — their bytes are streamed at write time, so a
        // DVD-sized tree never lands in memory.
        foreach (var file in Directory.EnumerateFiles(directory))
            nodes.Add(IsoBuilder.Node.FromPath(file));
        return nodes;
    }

    /// <summary>
    /// Lay the ISO out, then stream it into the CDI as a single Mode 1 / 2048
    /// data track. Memory use is independent of image size.
    /// </summary>
    private static CreateResult WriteIsoStreaming(
        string volumeId, IsoBuilder.IsoLayout layout, CdiVersion version, Stream output)
    {
        int isoSectors = layout.VolumeSectors;
        var track = new CdiWriter.TrackInput
        {
            Mode = CdiTrackMode.Mode1,
            SectorSize = CdiSectorSize.S2048,
            PregapSectors = 0,
            LengthSectors = (uint)isoSectors,
            StartLba = 0,
            Filename = $"{volumeId}.ISO",
            DataWriter = layout.WriteTo,
        };

        long startPos = output.CanSeek ? output.Position : 0;
        CdiWriter.Write(output, version, new[] { (IReadOnlyList<CdiWriter.TrackInput>)new[] { track } });
        long cdiBytes = output.CanSeek ? output.Position - startPos : 0;

        return new CreateResult(cdiBytes, isoSectors, layout.Warnings);
    }
}
