// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace DiscForge.Core.Floppy;

/// <summary>An index pulse recorded in a KryoFlux stream (out-of-band block type 2).</summary>
public sealed record KryoFluxIndex(uint StreamPosition, uint SampleCounter, uint IndexCounter);

/// <summary>A parsed KryoFlux raw stream (one track/side capture).</summary>
public sealed record KryoFluxStream
{
    /// <summary>Number of flux reversals decoded from the in-band stream.</summary>
    public required long FluxTransitions { get; init; }
    public required IReadOnlyList<KryoFluxIndex> Indices { get; init; }
    /// <summary>Hardware/firmware key=value info from KFInfo OOB blocks (incl. sck, ick).</summary>
    public required IReadOnlyDictionary<string, string> Info { get; init; }
    /// <summary>Sample clock in Hz (from KFInfo "sck"), used to turn flux ticks into time.</summary>
    public double? SampleClockHz { get; init; }
    public double? IndexClockHz { get; init; }
    public bool StreamEndSeen { get; init; }
    public uint StreamEndResult { get; init; }

    /// <summary>RPM inferred from two consecutive index pulses' sample counters.</summary>
    public double? Rpm
    {
        get
        {
            if (SampleClockHz is not { } sck || sck <= 0 || Indices.Count < 2) return null;
            long ticks = (long)Indices[1].SampleCounter - Indices[0].SampleCounter;
            if (ticks <= 0) return null;
            double seconds = ticks / sck;
            return seconds > 0 ? 60.0 / seconds : null;
        }
    }
}

/// <summary>
/// Reads a KryoFlux raw stream file — the flux-preservation format the KryoFlux hardware
/// writes (one file per track/side). It decodes the in-band flux cells (Flux1/2/3, Nop1/2/3,
/// Ovl16) and the out-of-band (OOB) blocks (StreamInfo, Index, StreamEnd, KFInfo), so DiscForge
/// can report flux-transition counts, index timing, RPM and the capture's hardware metadata —
/// the KryoFlux counterpart to the SCP reader. Clean-room, from the public KryoFlux stream
/// protocol. Pure flux preservation; no protection concerns.
/// </summary>
public static class KryoFluxStreamReader
{
    public static KryoFluxStream Parse(byte[] s)
    {
        long flux = 0;
        long overflow = 0;
        var indices = new List<KryoFluxIndex>();
        var info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool endSeen = false;
        uint endResult = 0;

        int i = 0;
        while (i < s.Length)
        {
            byte b = s[i];
            switch (b)
            {
                case >= 0x0E:                       // Flux1: single-byte value
                    flux++; overflow = 0; i += 1; break;
                case <= 0x07:                       // Flux2: (b<<8)|next
                    flux++; overflow = 0; i += 2; break;
                case 0x08: i += 1; break;           // Nop1
                case 0x09: i += 2; break;           // Nop2 (skip 1 following byte)
                case 0x0A: i += 3; break;           // Nop3 (skip 2 following bytes)
                case 0x0B: overflow += 0x10000; i += 1; break;   // Ovl16
                case 0x0C:                          // Flux3: next two bytes
                    flux++; overflow = 0; i += 3; break;
                case 0x0D:                          // OOB block
                    if (!ReadOob(s, ref i, indices, info, ref endSeen, ref endResult))
                        goto done;                  // EOF or malformed: stop cleanly
                    break;
            }
        }
        done:

        double? sck = TryDouble(info, "sck");
        double? ick = TryDouble(info, "ick");
        return new KryoFluxStream
        {
            FluxTransitions = flux,
            Indices = indices,
            Info = info,
            SampleClockHz = sck,
            IndexClockHz = ick,
            StreamEndSeen = endSeen,
            StreamEndResult = endResult,
        };
    }

    private static bool ReadOob(byte[] s, ref int i, List<KryoFluxIndex> indices,
                                Dictionary<string, string> info, ref bool endSeen, ref uint endResult)
    {
        if (i + 4 > s.Length) return false;
        byte type = s[i + 1];
        if (type == 0x0D) return false;             // End-of-file OOB: stop
        int size = BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(i + 2, 2));
        int body = i + 4;
        if (body + size > s.Length) return false;   // truncated
        var payload = s.AsSpan(body, size);

        switch (type)
        {
            case 0x02 when size >= 12:               // Index
                indices.Add(new KryoFluxIndex(
                    BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]),
                    BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, 4))));
                break;
            case 0x03 when size >= 8:                // StreamEnd
                endSeen = true;
                endResult = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
                break;
            case 0x04:                               // KFInfo: ASCII key=value list
                ParseKfInfo(payload, info);
                break;
            default: break;                          // StreamInfo (0x01) etc.: skipped
        }
        i = body + size;
        return true;
    }

    private static void ParseKfInfo(ReadOnlySpan<byte> payload, Dictionary<string, string> info)
    {
        string text = Encoding.ASCII.GetString(payload).TrimEnd('\0');
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0) info[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }
    }

    private static double? TryDouble(Dictionary<string, string> info, string key)
        => info.TryGetValue(key, out var v) &&
           double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
