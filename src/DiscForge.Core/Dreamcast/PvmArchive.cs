// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Dreamcast;

/// <summary>One texture inside a PVM archive: its archive index, the filename the archive records for it
/// (when the archive stores names), the byte offset the PVRT sits at, and the parsed texture header.</summary>
public sealed record PvmEntry
{
    public required int Index { get; init; }
    public string? Name { get; init; }
    public required long Offset { get; init; }
    public required PvrTexture Texture { get; init; }
}

/// <summary>A read Sega Dreamcast PVM (PVR-Multi) archive: the flags the header declares, how many
/// textures it claims to hold, and each embedded PVR texture actually found and parsed.</summary>
public sealed record PvmArchive
{
    public required int DeclaredCount { get; init; }
    public required bool HasGlobalIndices { get; init; }
    public required bool HasDimensions { get; init; }
    public required bool HasFormats { get; init; }
    public required bool HasFilenames { get; init; }
    public required IReadOnlyList<PvmEntry> Textures { get; init; }

    /// <summary>The number of textures found matches the header's declared count.</summary>
    public bool CountMatches => Textures.Count == DeclaredCount;
    /// <summary>Every embedded texture's own header checks out.</summary>
    public bool AllTexturesValid => Textures.All(e => e.Texture.Valid);

    public IReadOnlyList<string> Warnings
    {
        get
        {
            var w = new List<string>();
            if (!CountMatches)
                w.Add($"header declares {DeclaredCount} texture(s) but {Textures.Count} were found");
            int bad = Textures.Count(e => !e.Texture.Valid);
            if (bad > 0) w.Add($"{bad} of {Textures.Count} texture header(s) failed their own structural check");
            return w;
        }
    }

    public bool Valid => Warnings.Count == 0 && Textures.Count > 0;

    public string Summary()
    {
        string verdict = Textures.Count == 0 ? "no textures found"
            : Valid ? "archive OK" : string.Join("; ", Warnings);
        return $"PVM archive — {Textures.Count} texture(s){(HasFilenames ? " with filenames" : "")} — {verdict}.";
    }
}

/// <summary>
/// pvm-info — read (never rewrite) a Sega Dreamcast PVM archive, the container that bundles several PVR
/// textures into one file. It reads the PVMH header (flags + texture count), lifts the per-texture
/// filenames the archive records, and then parses each embedded PVR through <see cref="Pvr"/> so a
/// catalogue can list "12 textures: FONT.PVR 256×256 twiddled ARGB4444, …" and flag a truncated or
/// miscounted archive. Read-only content metadata, alongside DiscForge's other asset readers; it never
/// unpacks, detwiddles or renders anything.
/// </summary>
public static class Pvm
{
    private static readonly byte[] Pvmh = "PVMH"u8.ToArray();
    private static readonly byte[] Pvrt = "PVRT"u8.ToArray();

    private const int FlagGlobalIndex = 1 << 0;
    private const int FlagDimensions = 1 << 1;
    private const int FlagFormats = 1 << 2;
    private const int FlagFilenames = 1 << 3;

    public static PvmArchive Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x0C || !data[..4].SequenceEqual(Pvmh))
            throw new PvrFormatException("No \"PVMH\" signature — this is not a Dreamcast PVM archive.");

        // First-texture offset is stored as (offset − 8); the entry table fills the space before it.
        uint firstOffMinus8 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0x04, 4));
        int flags = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0x08, 2));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0x0A, 2));

        bool hasGi = (flags & FlagGlobalIndex) != 0;
        bool hasDim = (flags & FlagDimensions) != 0;
        bool hasFmt = (flags & FlagFormats) != 0;
        bool hasName = (flags & FlagFilenames) != 0;

        // --- entry table (names, in archive order) --------------------------
        var names = new List<string?>();
        int pos = 0x0C;
        for (int k = 0; k < count; k++)
        {
            if (pos + 2 > data.Length) break;
            pos += 2;                                   // entry index number
            string? name = null;
            if (hasName)
            {
                if (pos + 28 > data.Length) break;
                name = Ascii(data.Slice(pos, 28));
                pos += 28;
            }
            if (hasFmt) pos += 2;
            if (hasDim) pos += 2;
            if (hasGi) pos += 4;
            names.Add(name);
        }

        // --- embedded textures ---------------------------------------------
        long dataStart = firstOffMinus8 + 8L;
        // Trust the header offset when it points at a PVRT; otherwise fall back to scanning for the first.
        if (dataStart < 0 || dataStart + 4 > data.Length || !data.Slice((int)dataStart, 4).SequenceEqual(Pvrt))
        {
            int scan = FindFrom(data, Pvrt, Math.Max(0x0C, pos));
            dataStart = scan < 0 ? data.Length : scan;
        }

        var entries = new List<PvmEntry>();
        long at = dataStart;
        int idx = 0;
        while (at + 4 <= data.Length)
        {
            // Snap to the next PVRT (textures are 16-byte aligned; tolerate padding between them).
            if (!data.Slice((int)at, 4).SequenceEqual(Pvrt))
            {
                int next = FindFrom(data, Pvrt, (int)at);
                if (next < 0) break;
                at = next;
            }

            PvrTexture tex;
            try { tex = Pvr.Parse(data[(int)at..]); }
            catch (PvrFormatException) { break; }

            entries.Add(new PvmEntry
            {
                Index = idx,
                Name = idx < names.Count ? EmptyToNull(names[idx]) : null,
                Offset = at,
                Texture = tex,
            });
            idx++;

            // Advance past this chunk (magic + size field + declared body), then 16-byte align.
            long next2 = at + 8 + tex.DeclaredDataSize;
            next2 = (next2 + 15) & ~15L;
            at = next2 > at ? next2 : at + 16;
            if (count > 0 && entries.Count >= count) break;
        }

        return new PvmArchive
        {
            DeclaredCount = count,
            HasGlobalIndices = hasGi,
            HasDimensions = hasDim,
            HasFormats = hasFmt,
            HasFilenames = hasName,
            Textures = entries,
        };
    }

    public static PvmArchive ParseFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Parse(File.ReadAllBytes(path));
    }

    public static bool IsPvm(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[..4].SequenceEqual(Pvmh);

    public static string Render(PvmArchive a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var sb = new StringBuilder();
        sb.AppendLine(a.Summary());
        foreach (var e in a.Textures)
        {
            string name = e.Name is { Length: > 0 } ? e.Name : $"#{e.Index}";
            string ok = e.Texture.Valid ? "" : "  ⚠ " + string.Join("; ", e.Texture.Warnings);
            sb.AppendLine($"  {name,-16} {e.Texture.Width}×{e.Texture.Height} {e.Texture.PixelFormatName}, " +
                          $"{e.Texture.DataFormatName}{ok}");
        }
        return sb.ToString().TrimEnd();
    }

    private static int FindFrom(ReadOnlySpan<byte> data, ReadOnlySpan<byte> sig, int start)
    {
        int limit = data.Length - sig.Length;
        for (int i = Math.Max(0, start); i <= limit; i++)
            if (data.Slice(i, sig.Length).SequenceEqual(sig)) return i;
        return -1;
    }

    private static string Ascii(ReadOnlySpan<byte> field) =>
        Encoding.ASCII.GetString(field).TrimEnd('\0', ' ');

    private static string? EmptyToNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
