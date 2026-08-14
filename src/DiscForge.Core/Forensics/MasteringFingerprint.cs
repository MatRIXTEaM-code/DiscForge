// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Security.Cryptography;
using System.Text;

namespace DiscForge.Core.Forensics;

/// <summary>The mastering-identity of a disc image: the ISO 9660 volume-descriptor fields a mastering house and
/// tool stamp, plus structural hashes. Two genuine copies of a title share these; a re-mastered reproduction
/// keeps the game files but re-stamps the mastering identity, so this is where a counterfeit shows.</summary>
public sealed record MasteringFingerprint
{
    public string FormatVersion => "dmf/1";
    public required string Image { get; init; }
    public required int LogicalBlockSize { get; init; }
    public required long VolumeSpaceSize { get; init; }
    public required string SystemId { get; init; }
    public required string VolumeId { get; init; }
    public required string PublisherId { get; init; }
    public required string DataPreparerId { get; init; }
    public required string ApplicationId { get; init; }
    public required string CreationTime { get; init; }
    public required string ModificationTime { get; init; }
    /// <summary>SHA-256 over the whole Primary Volume Descriptor — the mastering stamp in one hash.</summary>
    public required string PvdHash { get; init; }
    /// <summary>SHA-256 over the image's final block — mastering tools fill trailing padding differently.</summary>
    public required string TailHash { get; init; }

    public string Summary() =>
        $"\"{VolumeId}\" — {VolumeSpaceSize:N0} blocks; app \"{ApplicationId}\", prepared \"{DataPreparerId}\", " +
        $"created {CreationTime}.";
}

/// <summary>How two images compare at the mastering level.</summary>
public enum MasteringVerdict
{
    /// <summary>Same title, identical mastering identity — consistent with two genuine copies (or the same dump).</summary>
    IdenticalMastering,
    /// <summary>Same volume (same title/size) but the mastering identity differs — a re-master / reproduction indicator.</summary>
    DivergentMastering,
    /// <summary>Different volume entirely (not the same title/edition).</summary>
    DifferentVolume,
}

public sealed record MasteringComparison
{
    public required MasteringVerdict Verdict { get; init; }
    public required IReadOnlyList<string> Divergences { get; init; }

    public string Summary() => Verdict switch
    {
        MasteringVerdict.IdenticalMastering => "IDENTICAL MASTERING — consistent with two genuine copies.",
        MasteringVerdict.DivergentMastering => $"DIVERGENT MASTERING — same title, but {Divergences.Count} mastering field(s) differ (a reproduction / re-master indicator).",
        _ => "DIFFERENT VOLUME — these are not the same title/edition.",
    };
}

/// <summary>
/// mastering-print — a disc's mastering fingerprint, and a genuine-vs-reproduction comparison. Redump records
/// ring/mould/IFPI codes by hand; nothing derives a structured, comparable mastering identity from the image
/// itself. This reads the ISO 9660 Primary Volume Descriptor — the system/volume/publisher/data-preparer/
/// application identifiers and the creation and modification timestamps that a mastering house and its authoring
/// tool stamp — plus a hash of that descriptor and of the trailing padding. Two pressings of the same title share
/// them; a reproduction that keeps the game files but was re-mastered (different tool, different date, different
/// padding) diverges here. It reads and characterises the disc's own metadata; it defeats and decrypts nothing.
/// </summary>
public static class MasteringPrinter
{
    // ISO 9660 Primary Volume Descriptor field offsets (within the 2048-byte descriptor).
    private const int SectorUser2048 = 2048;

    public static MasteringFingerprint Extract(string imagePath)
    {
        ArgumentNullException.ThrowIfNull(imagePath);
        var (pvd, tailHash) = ReadPvdAndTail(imagePath);

        string A(int off, int len) => Encoding.ASCII.GetString(pvd, off, len)
            .Replace('\0', ' ').TrimEnd();
        long U32(int off) => BitConverter.ToUInt32(pvd, off);   // little-endian field
        int U16(int off) => BitConverter.ToUInt16(pvd, off);

        return new MasteringFingerprint
        {
            Image = Path.GetFileName(imagePath),
            LogicalBlockSize = U16(128),
            VolumeSpaceSize = U32(80),
            SystemId = A(8, 32),
            VolumeId = A(40, 32),
            PublisherId = A(318, 128),
            DataPreparerId = A(446, 128),
            ApplicationId = A(574, 128),
            CreationTime = A(813, 17),
            ModificationTime = A(830, 17),
            PvdHash = Sha(pvd),
            TailHash = tailHash,
        };
    }

    public static MasteringComparison Compare(MasteringFingerprint a, MasteringFingerprint b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        // Same title/edition? Volume id + size are the primary identity.
        if (a.VolumeId != b.VolumeId || a.VolumeSpaceSize != b.VolumeSpaceSize)
        {
            var vol = new List<string>();
            if (a.VolumeId != b.VolumeId) vol.Add($"volume id: \"{a.VolumeId}\" vs \"{b.VolumeId}\"");
            if (a.VolumeSpaceSize != b.VolumeSpaceSize) vol.Add($"volume size: {a.VolumeSpaceSize:N0} vs {b.VolumeSpaceSize:N0} blocks");
            return new MasteringComparison { Verdict = MasteringVerdict.DifferentVolume, Divergences = vol };
        }

        var diffs = new List<string>();
        void Cmp(string field, string x, string y) { if (x != y) diffs.Add($"{field}: \"{x}\" vs \"{y}\""); }
        Cmp("system id", a.SystemId, b.SystemId);
        Cmp("publisher", a.PublisherId, b.PublisherId);
        Cmp("data preparer", a.DataPreparerId, b.DataPreparerId);
        Cmp("application (mastering tool)", a.ApplicationId, b.ApplicationId);
        Cmp("creation time", a.CreationTime, b.CreationTime);
        Cmp("modification time", a.ModificationTime, b.ModificationTime);
        if (a.PvdHash != b.PvdHash && diffs.Count == 0) diffs.Add("volume descriptor bytes differ (reserved/timestamp fields)");
        if (a.TailHash != b.TailHash) diffs.Add("trailing padding differs (different mastering fill)");

        return new MasteringComparison
        {
            Verdict = diffs.Count == 0 ? MasteringVerdict.IdenticalMastering : MasteringVerdict.DivergentMastering,
            Divergences = diffs,
        };
    }

    /// <summary>Read the 2048-byte Primary Volume Descriptor (sector 16) and hash the image's final block. Handles a
    /// cooked 2048 image and a raw 2352 data track (user data at offset 16 within each sector).</summary>
    private static (byte[] pvd, string tailHash) ReadPvdAndTail(string imagePath)
    {
        using var fs = File.OpenRead(imagePath);
        // Try cooked 2048 first, then raw 2352.
        foreach (var (sector, userOffset) in new[] { (2048, 0), (2352, 16) })
        {
            long pos = (long)16 * sector + userOffset;
            if (pos + SectorUser2048 > fs.Length) continue;
            var pvd = new byte[SectorUser2048];
            fs.Seek(pos, SeekOrigin.Begin);
            fs.ReadExactly(pvd, 0, SectorUser2048);
            if (pvd[0] == 0x01 && Encoding.ASCII.GetString(pvd, 1, 5) == "CD001")
            {
                // Tail: the final block at this sector size.
                int tail = Math.Min(sector, (int)fs.Length);
                var buf = new byte[tail];
                fs.Seek(fs.Length - tail, SeekOrigin.Begin);
                fs.ReadExactly(buf, 0, tail);
                return (pvd, Sha(buf));
            }
        }
        throw new InvalidDataException("No ISO 9660 Primary Volume Descriptor found (not a data disc image, or an unsupported layout).");
    }

    private static string Sha(byte[] data) => System.Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
