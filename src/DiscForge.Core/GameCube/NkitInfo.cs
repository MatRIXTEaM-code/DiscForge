// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.GameCube;

/// <summary>
/// What an NKit-scrubbed GameCube/Wii image records about the disc it came from. NKit shrinks a GC/Wii
/// image by removing reconstructable junk (and the Wii update partition), but stamps a small recovery
/// block into the disc header so the original can be rebuilt and, crucially, <b>matched to a Redump
/// entry without restoring it</b>: the block carries the source image's CRC32. Reading it is pure fixity
/// — DiscForge reports what the scrubbed file says it reconstructs to; it does not unscrub or decrypt.
/// </summary>
public sealed record NkitInfo
{
    public required bool IsNkit { get; init; }
    public string? Version { get; init; }
    /// <summary>"GameCube" or "Wii", from the disc header magic that NKit leaves in place.</summary>
    public string? Platform { get; init; }
    /// <summary>The 6-character game code at the start of the disc header (e.g. "GALE01").</summary>
    public string? GameId { get; init; }

    /// <summary>The original (pre-scrub) image's CRC32 — the field that matches this file to a known
    /// Redump dump without performing the reconstruction.</summary>
    public uint SourceCrc32 { get; init; }
    /// <summary>A correction CRC NKit stores so the scrubbed file's own CRC32 equals the source's.</summary>
    public uint NkitCrc { get; init; }
    /// <summary>The source-length field NKit records (32-bit, big-endian) at offset 0x210.</summary>
    public uint SourceLengthField { get; init; }
    /// <summary>GameCube "forced junk id" disc variant, when present.</summary>
    public uint ForcedJunkId { get; init; }
    /// <summary>CRC32 of the removed Wii update partition (0 when none was backed up).</summary>
    public uint UpdatePartitionCrc32 { get; init; }
    public bool HasUpdatePartitionBackup => UpdatePartitionCrc32 != 0;

    public string Summary()
    {
        if (!IsNkit) return "not an NKit image (no \"NKIT\" recovery block in the disc header).";
        string plat = Platform is { Length: > 0 } ? Platform : "GameCube/Wii";
        string id = GameId is { Length: > 0 } ? $" [{GameId}]" : "";
        var sb = new StringBuilder($"NKit-scrubbed {plat} image{id}");
        if (Version is { Length: > 0 }) sb.Append($" ({Version})");
        sb.Append($" — reconstructs to source CRC32 {SourceCrc32:X8}");
        if (HasUpdatePartitionBackup) sb.Append($"; update partition backed up (CRC32 {UpdatePartitionCrc32:X8})");
        sb.Append('.');
        return sb.ToString();
    }
}

/// <summary>Reader for the NKit recovery block that sits in a scrubbed GC/Wii disc header at 0x200.</summary>
public static class Nkit
{
    /// <summary>Offset of the NKit recovery block within the disc header.</summary>
    public const int HeaderOffset = 0x200;
    private static readonly byte[] Magic = "NKIT"u8.ToArray();

    /// <summary>Parse the NKit recovery block from the start of a GC/Wii image (needs ≥ 0x21C bytes).
    /// Returns a record with <see cref="NkitInfo.IsNkit"/> = false when no NKit block is present.</summary>
    public static NkitInfo Parse(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderOffset + 4 || !header.Slice(HeaderOffset, 4).SequenceEqual(Magic))
            return new NkitInfo { IsNkit = false };

        string? gameId = header.Length >= 6 ? Ascii(header[..6]) : null;
        string? platform = Platform(header);

        return new NkitInfo
        {
            IsNkit = true,
            Version = header.Length >= 0x208 ? Ascii(header.Slice(0x204, 4)) : null,
            Platform = platform,
            GameId = gameId,
            SourceCrc32 = U32(header, 0x208),
            NkitCrc = U32(header, 0x20C),
            SourceLengthField = U32(header, 0x210),
            ForcedJunkId = U32(header, 0x214),
            UpdatePartitionCrc32 = U32(header, 0x218),
        };
    }

    public static NkitInfo ParseFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var fs = File.OpenRead(path);
        int want = 0x21C;
        var buf = new byte[want];
        int read = 0;
        while (read < want)
        {
            int n = fs.Read(buf, read, want - read);
            if (n <= 0) break;
            read += n;
        }
        return Parse(buf.AsSpan(0, read));
    }

    /// <summary>True if these header bytes carry the NKit recovery block.</summary>
    public static bool IsNkit(ReadOnlySpan<byte> header) =>
        header.Length >= HeaderOffset + 4 && header.Slice(HeaderOffset, 4).SequenceEqual(Magic);

    // NKit leaves the console's disc-header magic in place: Wii 0x5D1C9EA3 at 0x18, GameCube 0xC2339F3D at 0x1C.
    private static string? Platform(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 0x1C && BinaryPrimitives.ReadUInt32BigEndian(h.Slice(0x18, 4)) == WiiDisc.Magic) return "Wii";
        if (h.Length >= 0x20 && BinaryPrimitives.ReadUInt32BigEndian(h.Slice(0x1C, 4)) == GcmReader.Magic) return "GameCube";
        return null;
    }

    private static uint U32(ReadOnlySpan<byte> h, int off) =>
        h.Length >= off + 4 ? BinaryPrimitives.ReadUInt32BigEndian(h.Slice(off, 4)) : 0;

    private static string Ascii(ReadOnlySpan<byte> s) => Encoding.ASCII.GetString(s).TrimEnd('\0', ' ');
}
