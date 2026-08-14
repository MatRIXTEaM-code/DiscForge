// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Burning;
using DiscForge.Core.Devices;
using DiscForge.Core.Mmc;
using DiscForge.Core.Reading;

namespace DiscForge.Core.Copying;

/// <summary>A disc-to-disc copy as requested.</summary>
public sealed record CopyJob
{
    /// <summary>The drive to read from.</summary>
    public required DriveCapabilities Source { get; init; }
    /// <summary>Where the copy goes — drives, and/or an image file.</summary>
    public required IReadOnlyList<BurnDestination> Destinations { get; init; }
    public bool Verify { get; init; }
    public int Copies { get; init; } = 1;
    /// <summary>Read data tracks raw (2352). Audio always forces raw regardless.</summary>
    public bool PreferRaw { get; init; }
}

/// <summary>A validated copy: what will be read, and what will be written.</summary>
public sealed record CopyPlan
{
    public required ReadPlan Read { get; init; }
    public required MultiBurnPlan Burn { get; init; }
    /// <summary>The shape the intermediate image will have.</summary>
    public required ImageShape Shape { get; init; }
    /// <summary>
    /// True when a destination drive is also the source: the disc must be read
    /// first, then swapped for a blank. Normal with a single drive.
    /// </summary>
    public required bool RequiresDiscSwap { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Bytes the intermediate image will occupy.</summary>
    public long ImageBytes => Read.TotalBytes;
}

/// <summary>
/// Plans a disc-to-disc copy: read the source, then burn what was read.
///
/// The point of planning it as a whole is that it can be **refused before a
/// single sector is read**. Reading an audio CD takes minutes; discovering only
/// afterwards that the burner can't write audio back is the worst possible time
/// to find out. Because the burn method depends only on the image's *shape*
/// (track count, sessions, audio), and the shape is known from the source's TOC,
/// the whole copy can be validated up front.
///
/// Sector modes are probed from the disc by the caller and passed in, for the
/// same reason: a Mode 2 Form 2 track cannot be read cooked at all, and finding
/// that out at LBA 5,828 rather than at plan time wastes the user's evening.
///
/// On copying "on the fly" (source straight to burner, no intermediate image):
/// deliberately not done. It was a disk-space optimisation from an era when
/// gigabytes cost more than discs. Today it is strictly worse — a reader that
/// stalls retrying a marginal sector starves the burner, and you lose the chance
/// to verify the image or salvage bad sectors before committing media. An
/// intermediate image is cheap, safer, and lets a single-drive copy work at all.
/// </summary>
public static class CopyPlanner
{
    public static CopyPlan Plan(DiscToc sourceToc, CopyJob job,
                                IReadOnlyDictionary<int, TrackSectorMode>? detected = null)
    {
        ArgumentNullException.ThrowIfNull(sourceToc);
        ArgumentNullException.ThrowIfNull(job);

        if (job.Destinations.Count == 0)
            throw new BurnNotSupportedException("Choose at least one destination for the copy.");
        if (job.Copies < 1)
            throw new BurnNotSupportedException("Copies must be at least 1.");

        var warnings = new List<string>();

        // 1) What can we read? The probed sector modes decide whether a cooked
        //    read is even possible — see ReadPlanner.
        var read = ReadPlanner.Plan(sourceToc, job.Source, job.PreferRaw, detected);
        warnings.AddRange(read.Warnings);

        // 2) What shape will the resulting image be? Known without reading it.
        // A copy must reproduce the source's gaps, which a TOC read cannot pin
        // down exactly — so treat a copied audio disc as having non-standard
        // gaps. That forces RAW DAO for a faithful copy, and says so plainly
        // rather than quietly standardising every gap to two seconds.
        var shape = new ImageShape(
            TrackCount: read.Tracks.Count,
            SessionCount: 1,                       // reads are single-session today
            HasAudio: read.Tracks.Any(t => t.IsAudio),
            HasData: read.Tracks.Any(t => !t.IsAudio),
            NonStandardGaps: read.Tracks.Any(t => t.IsAudio));

        // 3) Can the destinations write that shape?
        var burnJob = new MultiBurnJob
        {
            Destinations = job.Destinations,
            Write = true,
            Verify = job.Verify,
            Copies = job.Copies,
            Method = BurnMethodChoice.Auto,
        };
        var burn = BurnJobPlanner.PlanAll(shape, burnJob);

        // 4) Source doubling as a destination means a disc swap.
        bool swap = job.Destinations
            .OfType<BurnDestination.Drive>()
            .Any(d => string.Equals(d.Capabilities.DevicePath, job.Source.DevicePath,
                                    StringComparison.OrdinalIgnoreCase));
        if (swap)
            warnings.Add(
                "The source drive is also a destination: the disc will be read to an image " +
                "first, then you'll be asked to swap it for a blank.");

        if (shape.HasAudio)
            warnings.Add(
                "Audio discs are copied sector-exact, but sub-channel data (CD-TEXT, ISRC) is " +
                "not carried and gaps are reconstructed from the TOC.");

        // A Mode 2 disc reads back fine, but writing one is a different matter:
        // the burner has to be told these are Mode 2 sectors, and a plain data
        // burn would write them as Mode 1 and produce a disc no player accepts.
        // Say so now rather than after the media is spent.
        var form2 = read.Tracks
            .Where(t => t.Detected == TrackSectorMode.Mode2Form2)
            .Select(t => t.Number)
            .ToList();
        if (form2.Count > 0)
            warnings.Add(
                $"The source has Mode 2 Form 2 track(s) ({string.Join(", ", form2)}) — an SVCD, " +
                "Video CD or CD-i disc. The image is read faithfully, but writing it back " +
                "needs a burner and method that preserve Mode 2 sectors; a standard data burn " +
                "would not produce a playable disc. Verify the copy in a player before " +
                "trusting it.");

        return new CopyPlan
        {
            Read = read,
            Burn = burn,
            Shape = shape,
            RequiresDiscSwap = swap,
            Warnings = warnings,
        };
    }
}