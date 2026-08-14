// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Runtime.Versioning;
using DiscForge.Core.Mmc;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices.Burning;

/// <summary>
/// Replays a learned <see cref="BookTypeRecipe"/> — the drive's OWN captured book-type
/// (bitsetting) command — over SPTI, verbatim. DiscForge never fabricates vendor book-type
/// bytes; this issues exactly the CDB (and any DATA OUT) that a capture of the drive setting its
/// book type contained, so the replay is as trustworthy as the tool that was captured. A
/// vendor/model guard refuses to fire a recipe learned on one drive at a different one unless
/// explicitly forced, since these commands are drive-specific and meaningless (or harmful) on the
/// wrong hardware.
/// </summary>
[SupportedOSPlatform("windows")]
public static class BookTypeSetter
{
    public sealed record Result
    {
        public required string Drive { get; init; }
        public required string Command { get; init; }     // hex CDB
        public required bool Applied { get; init; }
        public string? Sense { get; init; }
        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Apply <paramref name="recipe"/> to the drive. Guards on vendor/model unless
    /// <paramref name="force"/>. Throws on a device error (with the drive's sense) so callers can
    /// surface exactly what the drive said.
    /// </summary>
    public static Result Apply(char driveLetter, BookTypeRecipe recipe, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (recipe.Cdb is null || recipe.Cdb.Length == 0)
            throw new ArgumentException("The recipe has no CDB to replay.", nameof(recipe));

        var notes = new List<string>();
        var caps = DriveDetector.Detect(driveLetter);

        // Vendor/model guard — a bitsetting command is drive-specific.
        bool vendorMatch = string.IsNullOrEmpty(recipe.DriveVendor) ||
                           Contains(caps.Vendor, recipe.DriveVendor) || Contains(recipe.DriveVendor, caps.Vendor);
        bool modelMatch = string.IsNullOrEmpty(recipe.DriveModel) ||
                          Contains(caps.Model, recipe.DriveModel) || Contains(recipe.DriveModel, caps.Model);
        if (!vendorMatch || !modelMatch)
        {
            string msg = $"Recipe was learned on '{recipe.DriveVendor} {recipe.DriveModel}' but drive {driveLetter}: is " +
                         $"'{caps.Vendor} {caps.Model}'. Bitsetting commands are drive-specific.";
            if (!force)
                throw new InvalidOperationException(msg + "  Re-run with --force only if you are certain the command applies.");
            notes.Add("WARNING (forced): " + msg);
        }

        using var dev = new SptiDevice(driveLetter);

        // A book-type command is a MODE SELECT / vendor SEND with parameter data (DATA OUT) — or,
        // for the fully-in-CDB variants, no data at all. Pick the direction from the recipe.
        var data = recipe.DataOut ?? Array.Empty<byte>();
        var direction = data.Length > 0 ? SptiDataDirection.Out : SptiDataDirection.None;
        var r = dev.SendCommand(recipe.Cdb, data, direction, 30);

        string cdbHex = MmcTrace.Hex(recipe.Cdb);
        if (!r.Success)
        {
            // Pull the real sense for a precise message.
            var sense = new byte[32];
            var rs = dev.SendCommand(MmcCommands.RequestSense(32), sense, SptiDataDirection.In, 10);
            string senseText = rs.Success && sense.Length >= 14
                ? $"key 0x{sense[2] & 0x0F:X1}, ASC 0x{sense[12]:X2}, ASCQ 0x{sense[13]:X2}"
                : r.Describe();
            throw new IOException($"The drive rejected the book-type command (CDB {cdbHex}): {senseText}. " +
                                  "The disc may need to be blank, or the recipe may target a different media class.");
        }

        notes.Add(recipe.Target is { } t
            ? $"Replayed the captured command for book type {t.Name()}."
            : "Replayed the captured book-type command.");
        notes.Add("Note: DiscForge issues the command verbatim; confirm the new book type with your drive's tool or a fresh burn.");

        return new Result
        {
            Drive = $"{driveLetter}: ({caps.Vendor} {caps.Model})",
            Command = cdbHex,
            Applied = true,
            Sense = null,
            Notes = notes,
        };
    }

    private static bool Contains(string? haystack, string? needle)
        => !string.IsNullOrEmpty(haystack) && !string.IsNullOrEmpty(needle) &&
           haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
