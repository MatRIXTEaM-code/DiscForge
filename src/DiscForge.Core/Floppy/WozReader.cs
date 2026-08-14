// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Util;

namespace DiscForge.Core.Floppy;

/// <summary>The INFO chunk of a WOZ image — the disk's physical parameters.</summary>
public sealed record WozInfo
{
    public required int InfoVersion { get; init; }
    /// <summary>1 = 5.25", 2 = 3.5".</summary>
    public required int DiskType { get; init; }
    public string DiskTypeName => DiskType == 1 ? "5.25\"" : DiskType == 2 ? "3.5\"" : "unknown";
    public bool WriteProtected { get; init; }
    /// <summary>Cross-track synchronization was used when imaging (protection-relevant).</summary>
    public bool Synchronized { get; init; }
    /// <summary>MC3470 "fake"/weak bits were removed (emulator must regenerate them).</summary>
    public bool Cleaned { get; init; }
    public string Creator { get; init; } = "";
    public int Sides { get; init; }
    /// <summary>0 unknown, 1 = 16-sector, 2 = 13-sector, 3 = both (5.25 only).</summary>
    public int BootSectorFormat { get; init; }
    /// <summary>Optimal bit timing in 125 ns units (32 = 4 µs, the 5.25 standard).</summary>
    public int OptimalBitTiming { get; init; }
    public int RequiredRamKb { get; init; }
    public int LargestTrackBlocks { get; init; }
}

/// <summary>A single stored track bitstream (from the TRKS chunk).</summary>
public sealed record WozTrack(int Index, int StartBlock, int BlockCount, long BitCount)
{
    public long ByteLength => (BitCount + 7) / 8;
}

/// <summary>A parsed WOZ (Apple II) disk image.</summary>
public sealed record WozDisk
{
    public required int FormatVersion { get; init; }        // 1, 2 or 3
    public required WozInfo Info { get; init; }
    /// <summary>160-entry quarter-track (5.25) / track-side (3.5) → TRKS index map; 0xFF = none.</summary>
    public required byte[] Tmap { get; init; }
    public required IReadOnlyList<WozTrack> Tracks { get; init; }
    public IReadOnlyDictionary<string, string> Meta { get; init; } =
        new Dictionary<string, string>();
    public bool HasFlux { get; init; }
    /// <summary>Whether the header CRC32 matched (true if present and valid, or 0 = not set).</summary>
    public required bool CrcValid { get; init; }
    public bool CrcPresent { get; init; }

    /// <summary>Distinct populated quarter-tracks (TMAP entries that point at a track).</summary>
    public int MappedPositions => Tmap.Count(b => b != 0xFF);
}

/// <summary>
/// Reads an Applesauce WOZ disk image — the gold-standard Apple II archival format,
/// which captures a floppy's exact bitstream *including* copy protection (weak bits,
/// cross-track synchronization, quarter-track alignment) without defeating it. This
/// reader parses the container (INFO / TMAP / TRKS / META, and notes an optional FLUX
/// chunk) and validates the header CRC-32; the raw track bitstreams are exposed as-is.
/// Clean-room, from the public WOZ 2.1 reference. WOZ preserves protection faithfully;
/// it never circumvents it.
/// </summary>
public static class WozReader
{
    private const int Block = 512;

    public static bool IsWoz(ReadOnlySpan<byte> head)
        => head.Length >= 4 &&
           (head[0] == 'W' && head[1] == 'O' && head[2] == 'Z' && (head[3] == '1' || head[3] == '2'));

    public static WozDisk Read(Stream s)
    {
        s.Position = 0;
        var all = new byte[s.Length];
        s.ReadExactly(all, 0, all.Length);
        return Parse(all);
    }

    public static WozDisk Parse(byte[] all)
    {
        if (all.Length < 12 || !IsWoz(all))
            throw new InvalidDataException("Not a WOZ image (missing WOZ1/WOZ2 signature).");
        int version = all[3] - '0';
        if (all[4] != 0xFF || all[5] != 0x0A || all[6] != 0x0D || all[7] != 0x0A)
            throw new InvalidDataException("WOZ header integrity bytes are wrong (corrupt or not a WOZ).");

        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(8, 4));
        bool crcPresent = storedCrc != 0;
        bool crcValid = !crcPresent || Crc32.Compute(all.AsSpan(12)) == storedCrc;

        if (version == 1)
            // WOZ1 uses a different INFO/TRKS geometry; recognise it honestly rather than
            // mis-decode. (Applesauce emits v2+; v1 decode is a documented follow-up.)
            return new WozDisk
            {
                FormatVersion = 1,
                Info = new WozInfo { InfoVersion = 1, DiskType = 1 },
                Tmap = new byte[160],
                Tracks = Array.Empty<WozTrack>(),
                CrcValid = crcValid,
                CrcPresent = crcPresent,
            };

        WozInfo? info = null;
        byte[] tmap = new byte[160];
        var tracks = new List<WozTrack>();
        var meta = new Dictionary<string, string>();
        bool hasFlux = false;

        int pos = 12;
        while (pos + 8 <= all.Length)
        {
            string id = Encoding.ASCII.GetString(all, pos, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(pos + 4, 4));
            int body = pos + 8;
            if (body + (long)size > all.Length) break;      // truncated chunk: stop cleanly
            var chunk = all.AsSpan(body, (int)size);

            switch (id)
            {
                case "INFO": info = ParseInfo(chunk); break;
                case "TMAP": chunk[..Math.Min(160, chunk.Length)].CopyTo(tmap); break;
                case "TRKS": ParseTrks(chunk, tracks); break;
                case "META": ParseMeta(chunk, meta); break;
                case "FLUX": hasFlux = true; break;
                default: break;                              // unknown chunk: skip (forward-compat)
            }
            pos = body + (int)size;
        }

        if (info is null) throw new InvalidDataException("WOZ image has no INFO chunk.");
        return new WozDisk
        {
            FormatVersion = version,
            Info = info,
            Tmap = tmap,
            Tracks = tracks,
            Meta = meta,
            HasFlux = hasFlux,
            CrcValid = crcValid,
            CrcPresent = crcPresent,
        };
    }

    private static WozInfo ParseInfo(ReadOnlySpan<byte> c)
    {
        string creator = c.Length >= 37
            ? Encoding.UTF8.GetString(c.Slice(5, 32).ToArray()).TrimEnd(' ', '\0') : "";
        return new WozInfo
        {
            InfoVersion = c.Length > 0 ? c[0] : 0,
            DiskType = c.Length > 1 ? c[1] : 0,
            WriteProtected = c.Length > 2 && c[2] == 1,
            Synchronized = c.Length > 3 && c[3] == 1,
            Cleaned = c.Length > 4 && c[4] == 1,
            Creator = creator,
            Sides = c.Length > 37 ? c[37] : 0,
            BootSectorFormat = c.Length > 38 ? c[38] : 0,
            OptimalBitTiming = c.Length > 39 ? c[39] : 0,
            RequiredRamKb = c.Length > 43 ? BinaryPrimitives.ReadUInt16LittleEndian(c.Slice(42, 2)) : 0,
            LargestTrackBlocks = c.Length > 45 ? BinaryPrimitives.ReadUInt16LittleEndian(c.Slice(44, 2)) : 0,
        };
    }

    private static void ParseTrks(ReadOnlySpan<byte> c, List<WozTrack> tracks)
    {
        // 160 TRK entries × 8 bytes: start block (u16), block count (u16), bit count (u32).
        for (int i = 0; i < 160; i++)
        {
            int off = i * 8;
            if (off + 8 > c.Length) break;
            int startBlock = BinaryPrimitives.ReadUInt16LittleEndian(c.Slice(off, 2));
            int blockCount = BinaryPrimitives.ReadUInt16LittleEndian(c.Slice(off + 2, 2));
            long bitCount = BinaryPrimitives.ReadUInt32LittleEndian(c.Slice(off + 4, 4));
            if (startBlock == 0 && blockCount == 0 && bitCount == 0) continue;   // empty slot
            tracks.Add(new WozTrack(i, startBlock, blockCount, bitCount));
        }
    }

    private static void ParseMeta(ReadOnlySpan<byte> c, Dictionary<string, string> meta)
    {
        string text = Encoding.UTF8.GetString(c.ToArray());
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0) continue;
            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            meta[line[..tab]] = line[(tab + 1)..].TrimEnd('\r');
        }
    }
}
