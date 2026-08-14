// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Runtime.Versioning;
using DiscForge.Core.Mmc;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices.Burning;

/// <summary>
/// A read-only write diagnostic: asks the drive where it expects the next write to go
/// (READ DISC INFORMATION + READ TRACK INFORMATION) instead of assuming. The
/// next-writable-address (NWA) it returns is the ground truth for the SPTI raw-DAO write's
/// start LBA — replacing the guesses (0, -150) that stalled the write. Purely read commands;
/// no disc is written.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WriteInfoDiagnostic
{
    public sealed record Report
    {
        public string DiscStatus { get; init; } = "unknown";
        public int Sessions { get; init; }
        public int FirstTrack { get; init; }
        public int LastTrack { get; init; }
        public byte DiscType { get; init; }

        public bool TrackInfoOk { get; init; }
        public int TrackNumber { get; init; }
        public bool Blank { get; init; }
        public bool NwaValid { get; init; }
        public long NextWritableAddress { get; init; }   // signed: -150 shows as -150
        public long TrackStart { get; init; }
        public long FreeBlocks { get; init; }
        public int TrackMode { get; init; }
        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    }

    public static Report Read(char driveLetter)
    {
        using var dev = new SptiDevice(driveLetter);
        var notes = new List<string>();

        // ── READ DISC INFORMATION ─────────────────────────────────────────────────────────
        string discStatus = "unknown";
        int sessions = 0, firstTrack = 0, lastTrack = 0;
        byte discType = 0;
        var di = new byte[34];
        var rdi = dev.SendCommand(MmcCommands.ReadDiscInformation(34), di, SptiDataDirection.In, 20);
        if (rdi.Success)
        {
            discStatus = (di[2] & 0x03) switch
            {
                0 => "empty (blank)",
                1 => "incomplete / appendable",
                2 => "complete / finalized",
                _ => "reserved",
            };
            firstTrack = di[3];
            sessions = di[4] | (di[9] << 8);
            lastTrack = di[6] | (di[11] << 8);
            discType = di[8];
        }
        else notes.Add("READ DISC INFORMATION failed: " + rdi.Describe());

        // ── READ TRACK INFORMATION ────────────────────────────────────────────────────────
        // On a blank disc the writable track is the invisible/incomplete one. Try track 1
        // first, then the "invisible track" (0xFF), then LBA 0.
        var (ti, which) = TryReadTrackInfo(dev, notes);
        bool tiOk = ti is not null;
        int trackNo = 0, trackMode = 0;
        bool blank = false, nwaV = false;
        long nwa = 0, trackStart = 0, freeBlocks = 0;
        if (ti is not null)
        {
            trackNo = ti[2];
            trackMode = ti[5] & 0x0F;
            blank = (ti[6] & 0x40) != 0;                 // byte 6 bit 6 = Blank
            nwaV = (ti[7] & 0x01) != 0;                  // byte 7 bit 0 = NWA_V
            trackStart = ReadS32(ti, 8);
            nwa = ReadS32(ti, 12);
            freeBlocks = ReadS32(ti, 16);
            notes.Add($"track info read via {which}.");
        }

        return new Report
        {
            DiscStatus = discStatus,
            Sessions = sessions,
            FirstTrack = firstTrack,
            LastTrack = lastTrack,
            DiscType = discType,
            TrackInfoOk = tiOk,
            TrackNumber = trackNo,
            Blank = blank,
            NwaValid = nwaV,
            NextWritableAddress = nwa,
            TrackStart = trackStart,
            FreeBlocks = freeBlocks,
            TrackMode = trackMode,
            Notes = notes,
        };
    }

    private static (byte[]? data, string which) TryReadTrackInfo(SptiDevice dev, List<string> notes)
    {
        // (addressType, number, label)
        var attempts = new (byte type, uint num, string label)[]
        {
            (1, 1, "track 1"),
            (1, 0xFF, "the invisible track (0xFF)"),
            (0, 0, "LBA 0"),
        };
        foreach (var (type, num, label) in attempts)
        {
            var buf = new byte[40];
            var r = dev.SendCommand(MmcCommands.ReadTrackInformation(type, num, 40), buf, SptiDataDirection.In, 20);
            if (r.Success) return (buf, label);
            notes.Add($"READ TRACK INFORMATION ({label}) failed: {r.Describe()}");
        }
        return (null, "");
    }

    private static long ReadS32(byte[] b, int off) =>
        b.Length >= off + 4 ? BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(off, 4)) : 0;
}
