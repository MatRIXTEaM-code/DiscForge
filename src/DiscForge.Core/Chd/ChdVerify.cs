// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Chd;

/// <summary>The outcome of verifying a CHD's integrity.</summary>
public enum ChdVerifyVerdict
{
    /// <summary>Decompressed cleanly and matched the CHD's stored SHA-1.</summary>
    Valid,
    /// <summary>Decompressed cleanly, but the CHD stores no SHA-1 to check against (an uncompressed CHD).</summary>
    Unverified,
    /// <summary>A hunk's map CRC-16 or the whole-image SHA-1 did not match — the data is damaged.</summary>
    Corrupt,
    /// <summary>The CHD could not be processed (unsupported version/codec, or too large to verify in memory).</summary>
    Unsupported,
}

/// <summary>The result of a CHD integrity check.</summary>
public sealed record ChdVerifyReport
{
    public required ChdVerifyVerdict Verdict { get; init; }
    public int Version { get; init; }
    public IReadOnlyList<string> Compressors { get; init; } = Array.Empty<string>();
    public long LogicalBytes { get; init; }
    public int HunkBytes { get; init; }
    public long HunkCount { get; init; }
    public bool IsCd { get; init; }
    public int TrackCount { get; init; }
    public string Sha1 { get; init; } = "";
    public required string Detail { get; init; }

    public bool Ok => Verdict is ChdVerifyVerdict.Valid or ChdVerifyVerdict.Unverified;

    public string Summary()
    {
        var sb = new StringBuilder();
        string verdict = Verdict switch
        {
            ChdVerifyVerdict.Valid => "VALID — decompressed cleanly and matches the stored SHA-1.",
            ChdVerifyVerdict.Unverified => "UNVERIFIED — decompressed cleanly, but this CHD stores no SHA-1 to check.",
            ChdVerifyVerdict.Corrupt => "CORRUPT — " + Detail,
            _ => "UNSUPPORTED — " + Detail,
        };
        sb.AppendLine($"CHD verify: {verdict}");
        if (Version > 0)
        {
            var codecs = Compressors.Where(c => c != "none").ToList();
            sb.AppendLine($"  CHD v{Version}, codecs [{string.Join(", ", codecs.Count == 0 ? new[] { "none (uncompressed)" } : codecs)}], " +
                          $"{LogicalBytes:N0} bytes in {HunkCount:N0} hunk(s) of {HunkBytes:N0}" +
                          (IsCd ? $", {TrackCount} CD track(s)" : ""));
            if (Sha1.Length > 0) sb.AppendLine($"  stored SHA-1: {Sha1.ToLowerInvariant()}");
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// chd-verify — check a CHD's integrity without extracting it. CHD is the compressed image format the
/// emulation and preservation world runs on (MAME, RetroArch, Redump archives), and a stored CHD can rot
/// or arrive truncated like any file. This decompresses every hunk, checks each against its map's CRC-16,
/// and confirms the whole decompressed image matches the SHA-1 the CHD stores of itself — the same proof
/// chdman's own verify performs — then reports one verdict. It reads and checks, and writes nothing.
/// </summary>
public static class ChdVerify
{
    public static ChdVerifyReport Check(byte[] chd, byte[][]? parents = null)
    {
        ArgumentNullException.ThrowIfNull(chd);
        parents ??= Array.Empty<byte[]>();

        // Header first — an unreadable header is "unsupported", not "corrupt data".
        ChdInfo info;
        try
        {
            info = ChdReader.Read(chd);
        }
        catch (ChdFormatException ex)
        {
            return new ChdVerifyReport { Verdict = ChdVerifyVerdict.Unsupported, Detail = ex.Message };
        }

        long hunkCount = info.HunkBytes > 0 ? (info.LogicalBytes + info.HunkBytes - 1) / info.HunkBytes : 0;
        string sha1 = chd.Length >= 0x54 ? System.Convert.ToHexString(chd.AsSpan(0x40, 20)) : "";
        bool hasStoredSha1 = chd.Length >= 0x54 && chd.AsSpan(0x40, 20).ToArray().Any(b => b != 0);

        ChdVerifyReport Report(ChdVerifyVerdict v, string detail) => new()
        {
            Verdict = v, Version = info.Version, Compressors = info.Compressors,
            LogicalBytes = info.LogicalBytes, HunkBytes = info.HunkBytes, HunkCount = hunkCount,
            IsCd = info.IsCd, TrackCount = info.Tracks.Count, Sha1 = sha1, Detail = detail,
        };

        try
        {
            // Decompress everything: this checks each hunk's map CRC-16 and, at the end, the whole-image
            // SHA-1 — both throw ChdFormatException on a mismatch.
            if (info.IsCd) _ = ChdExtractor.ExtractCd(chd, parents);
            else _ = ChdHdExtractor.Extract(chd, parents.Length == 0 ? null : parents[0]);

            return hasStoredSha1
                ? Report(ChdVerifyVerdict.Valid, "verified")
                : Report(ChdVerifyVerdict.Unverified, "no stored SHA-1");
        }
        catch (ChdFormatException ex)
        {
            // "too large to extract in memory" is a tool limitation, not a corrupt image.
            bool limitation = ex.Message.Contains("too large", StringComparison.OrdinalIgnoreCase);
            return Report(limitation ? ChdVerifyVerdict.Unsupported : ChdVerifyVerdict.Corrupt, ex.Message);
        }
    }
}
