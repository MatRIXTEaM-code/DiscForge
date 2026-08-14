// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Mmc;

/// <summary>The write type field of the MMC Write Parameters mode page (byte 2, bits 3–0).</summary>
public enum CdWriteType : byte
{
    /// <summary>Packet / incremental (rewritable, multi-session-style).</summary>
    Packet = 0,
    /// <summary>Track-at-once — per-track, standard two-second gaps.</summary>
    TrackAtOnce = 1,
    /// <summary>Session-at-once (a.k.a. DAO for a single session).</summary>
    SessionAtOnce = 2,
    /// <summary>Raw — the whole disc written in one pass, sub-channel and all.</summary>
    Raw = 3,
    /// <summary>Layer-jump recording (DVD-R DL).</summary>
    LayerJump = 4,
}

/// <summary>
/// The MMC "Write Parameters" mode page (page code 0x05) — the low-level write knobs a
/// burner exposes: write type (DAO/TAO/RAW), the test-write (laser-off simulation) bit,
/// buffer-underrun-free recording (BURN-Proof), link size, track/data block type and
/// session format. This is the pure builder for the page bytes; a MODE SELECT(10)
/// (<see cref="MmcCommands.ModeSelect10"/>) carries it to the drive.
///
/// Byte layout (page body, per MMC-5 Table "Write Parameters Mode Page"):
///   0  PS | Page Code (0x05)
///   1  Page Length (0x32 = 50)
///   2  BUFE(6) | LS_V(5) | Test Write(4) | Write Type(3:0)
///   3  Multi-session(7:6) | FP(5) | Copy(4) | Track Mode(3:0)
///   4  Data Block Type(3:0)
///   5  Link Size
///   7  Host Application Code(5:0)
///   8  Session Format
///  10..13 Packet Size (fixed-packet only)
///  14..15 Audio Pause Length (default 150)
///  16..31 Media Catalog Number   32..47 ISRC   48..51 sub-header
/// </summary>
public sealed record WriteParametersPage
{
    public CdWriteType WriteType { get; init; } = CdWriteType.SessionAtOnce;
    /// <summary>Simulate the burn with the laser off (nothing is written).</summary>
    public bool TestWrite { get; init; }
    /// <summary>Buffer-Underrun-Free recording (BURN-Proof / JustLink).</summary>
    public bool BufferUnderrunFree { get; init; }
    /// <summary>Set when <see cref="LinkSize"/> is meaningful (LS_V).</summary>
    public bool LinkSizeValid { get; init; }
    public byte LinkSize { get; init; }
    /// <summary>Multi-session field (0 = no B0 pointer / next-session-not-allowed … 3 = multi-session).</summary>
    public byte MultiSession { get; init; }
    public bool FixedPacket { get; init; }
    /// <summary>Track mode = the Q sub-channel CONTROL nibble (0x04 = data, 0x00 = 2-ch audio).</summary>
    public byte TrackMode { get; init; } = 0x04;
    /// <summary>Data block type (8 = Mode 1/2048, 10 = Mode 2 Form 1/2048, 0 = raw 2352, …).</summary>
    public byte DataBlockType { get; init; } = 8;
    /// <summary>Session format (0x00 = CD-DA/data, 0x10 = CD-I, 0x20 = CD-ROM XA).</summary>
    public byte SessionFormat { get; init; }
    public ushort AudioPauseLength { get; init; } = 150;

    /// <summary>The 52-byte mode page (2-byte header + 50-byte body).</summary>
    public byte[] Build()
    {
        if (LinkSizeValid && !BufferUnderrunFree) { /* LS_V is only honoured with certain drives; allowed */ }

        var p = new byte[52];
        p[0] = 0x05;               // page code (PS = 0)
        p[1] = 0x32;               // page length = 50
        p[2] = (byte)(((BufferUnderrunFree ? 1 : 0) << 6)
                    | ((LinkSizeValid ? 1 : 0) << 5)
                    | ((TestWrite ? 1 : 0) << 4)
                    | ((byte)WriteType & 0x0F));
        p[3] = (byte)(((MultiSession & 0x03) << 6)
                    | ((FixedPacket ? 1 : 0) << 5)
                    | (TrackMode & 0x0F));
        p[4] = (byte)(DataBlockType & 0x0F);
        p[5] = LinkSize;
        p[8] = SessionFormat;
        p[14] = (byte)(AudioPauseLength >> 8);
        p[15] = (byte)AudioPauseLength;
        return p;
    }
}
