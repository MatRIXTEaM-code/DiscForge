// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Files;

/// <summary>How the filesystem views on a disc relate to one another.</summary>
public enum CrossCheckVerdict
{
    /// <summary>No readable filesystem was found.</summary>
    None,
    /// <summary>Exactly one filesystem view — nothing to cross-check.</summary>
    Single,
    /// <summary>Every view delivers the same set of file contents.</summary>
    Agree,
    /// <summary>Two or more views describe different content — a red flag.</summary>
    Divergent,
    /// <summary>A filesystem is declared on the disc but could not be read (e.g. a truncated dump).</summary>
    Incomplete,
}

/// <summary>One filesystem view of a disc, with its file tally.</summary>
public sealed record CrossCheckView
{
    public required string Kind { get; init; }
    public required string? VolumeId { get; init; }
    public required int FileCount { get; init; }
    public required long TotalBytes { get; init; }
    /// <summary>Set when the filesystem was detected on the disc but could not be parsed.</summary>
    public string? Error { get; init; }
}

/// <summary>A single way in which two views fail to line up.</summary>
public sealed record CrossCheckDiscrepancy(string Kind, string Detail);

/// <summary>
/// The result of cross-checking every filesystem view a disc carries. On a bridge or
/// hybrid disc the ISO 9660 and UDF directory structures are independent but should
/// describe the very same files; a mismatch means a truncated dump, a tampered image,
/// or content that is reachable from one filesystem and hidden from the other.
/// </summary>
public sealed record CrossCheckResult
{
    public required CrossCheckVerdict Verdict { get; init; }
    public required IReadOnlyList<CrossCheckView> Views { get; init; }
    public required IReadOnlyList<CrossCheckDiscrepancy> Discrepancies { get; init; }

    public string Summary()
    {
        var sb = new StringBuilder();
        string verdict = Verdict switch
        {
            CrossCheckVerdict.Agree => "AGREE — every filesystem view describes the same files",
            CrossCheckVerdict.Divergent => "DIVERGENT — the filesystem views describe DIFFERENT content",
            CrossCheckVerdict.Incomplete => "INCOMPLETE — a filesystem is present but could not be read",
            CrossCheckVerdict.Single => "SINGLE — one ISO 9660/UDF filesystem (no bridge to cross-check; see any hybrid catalogue below)",
            _ => "NONE — no readable filesystem found",
        };
        sb.AppendLine(verdict);
        foreach (var v in Views)
        {
            if (v.Error is not null)
                sb.AppendLine($"  {v.Kind}: UNREADABLE — {v.Error}");
            else
                sb.AppendLine($"  {v.Kind}: {v.FileCount:N0} file(s), {v.TotalBytes:N0} bytes" +
                              (v.VolumeId is null ? "" : $", volume \"{v.VolumeId}\""));
        }
        foreach (var d in Discrepancies)
            sb.AppendLine($"  ! {d.Kind}: {d.Detail}");
        return sb.ToString().TrimEnd();
    }
}
