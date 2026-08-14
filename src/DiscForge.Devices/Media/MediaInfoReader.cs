// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Runtime.Versioning;
using DiscForge.Core.Media;
using DiscForge.Core.Mmc;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices.Media;

/// <summary>Everything a drive will say about itself and the disc inside it.</summary>
public sealed record MediaInfoReport
{
    public MediaIdentity? Identity { get; init; }
    public DriveCapabilityPage? Capabilities { get; init; }
    /// <summary>Why a piece of information is missing, when it is. A pressed disc
    /// having no ATIP is a fact about the disc, not a failure — say which.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// Asks a drive to identify itself and the disc it's holding.
///
/// Each query is independent and any of them may legitimately fail: pressed
/// discs have no ATIP, CDs have no physical format descriptor, and plenty of
/// drives refuse ADIP outright. A refusal is recorded as a note and the rest of
/// the report is still produced — the alternative, failing the whole thing
/// because one optional structure was absent, would make the feature useless on
/// exactly the discs people want to identify.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MediaInfoReader
{
    public static MediaInfoReport Read(char driveLetter)
    {
        var notes = new List<string>();
        MediaIdentity? identity = null;
        DriveCapabilityPage? caps = null;

        try
        {
            using var dev = new SptiDevice(driveLetter);

            caps = ReadCapabilities(dev, notes);

            // ATIP first: it's the CD answer, and its absence is informative.
            identity = ReadAtip(dev, notes);

            // No ATIP means either a pressed CD or non-CD media. Try the DVD/BD
            // structures before concluding anything.
            identity ??= ReadDiscStructure(dev, notes);

            if (identity is null)
                notes.Add("The disc reports neither ATIP nor a physical format descriptor. " +
                          "That is normal for a pressed CD — those carry no manufacturing " +
                          "identity a drive can read.");
        }
        catch (Exception ex)
        {
            notes.Add($"Could not open the drive: {ex.Message}");
        }

        return new MediaInfoReport
        {
            Identity = identity,
            Capabilities = caps,
            Notes = notes,
        };
    }

    private static DriveCapabilityPage? ReadCapabilities(SptiDevice dev, List<string> notes)
    {
        var buffer = new byte[512];
        var r = dev.SendCommand(MmcCommands.ModeSense10(0x2A, 512), buffer,
                                SptiDataDirection.In, timeoutSeconds: 15);
        if (!r.Success)
        {
            notes.Add($"MODE SENSE page 2Ah refused: {r.Describe()}. Capability detail " +
                      "is unavailable on this drive.");
            return null;
        }

        var page = DriveCapabilityPageParser.Parse(buffer);
        if (page is null)
            notes.Add("The drive answered MODE SENSE but the 2Ah page did not parse — " +
                      "the response may be truncated or non-standard.");
        return page;
    }

    private static MediaIdentity? ReadAtip(SptiDevice dev, List<string> notes)
    {
        var buffer = new byte[32];
        var r = dev.SendCommand(MmcCommands.ReadTocFormat(MmcCommands.TocFormat.Atip, 32),
                                buffer, SptiDataDirection.In, timeoutSeconds: 15);
        if (!r.Success)
        {
            // A pressed disc has no ATIP and the drive says so. Only worth
            // mentioning if the refusal wasn't the expected one.
            if (r.Asc is not (0x64 or 0x24 or 0x30))
                notes.Add($"ATIP read refused: {r.Describe()}.");
            return null;
        }

        var id = MediaIdentityParser.ParseAtip(buffer);
        if (id is not null) notes.AddRange(id.Notes);
        return id;
    }

    private static MediaIdentity? ReadDiscStructure(SptiDevice dev, List<string> notes)
    {
        var buffer = new byte[68];
        var r = dev.SendCommand(
            MmcCommands.ReadDiscStructure(MmcCommands.DiscStructureFormat.PhysicalFormat,
                                          allocationLength: 68),
            buffer, SptiDataDirection.In, timeoutSeconds: 15);
        if (!r.Success) return null;

        var id = MediaIdentityParser.ParsePhysicalFormat(buffer);
        if (id is null) return null;

        var extraNotes = new List<string>(id.Notes);

        // +R/+RW keep their media ID in ADIP, which is drive-dependent — plenty
        // of drives simply refuse. Worth one attempt when the ID is missing.
        if (id.MediaId is null)
        {
            var adip = new byte[192];
            var ar = dev.SendCommand(
                MmcCommands.ReadDiscStructure(MmcCommands.DiscStructureFormat.Adip,
                                              allocationLength: 192),
                adip, SptiDataDirection.In, timeoutSeconds: 15);

            if (ar.Success)
            {
                var mediaId = MediaIdentityParser.ParseAdipMediaId(adip);
                if (mediaId is not null)
                    id = id with
                    {
                        MediaId = mediaId,
                        Manufacturer = DvdMediaIds.Lookup(mediaId),
                    };
            }
            else
            {
                extraNotes.Add("This drive does not report ADIP, so the blank's media ID " +
                               "is unavailable. Book type and capacity are still accurate.");
            }
        }

        notes.AddRange(extraNotes);
        return id with { Notes = extraNotes };
    }
}