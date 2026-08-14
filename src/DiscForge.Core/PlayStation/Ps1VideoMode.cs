// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using DiscForge.Core.Patch;

namespace DiscForge.Core.PlayStation;

public enum PsxVideoMode { Ntsc, Pal }

/// <summary>
/// PlayStation video-mode (PAL/NTSC) conversion — the PAL4U / Zapper 2000 job,
/// done the clean-room way. This works on the PS1 GPU's own display-mode command
/// and changes ONLY the video timing bit; it is display-mode conversion, not
/// region unlocking, protection removal, or cheat-code generation. (Vertical
/// re-centring after a conversion — "Y-Fix" — is PAR cheat-code territory and is
/// deliberately left alone, consistent with DiscForge's clean-room rule,
/// docs/COMPARISON.md §13.)
///
/// The GPU sets the display mode with GP1 command 0x08. The 32-bit command word is
/// <c>0x08000000 | param</c>, where the low byte's bit 3 is the video mode
/// (0 = NTSC/60 Hz, 1 = PAL/50 Hz). Public GP1(08h) description:
///   bits 0-1 horizontal resolution, bit 2 vertical resolution, bit 3 video mode,
///   bit 4 colour depth, bit 5 interlace, bit 6 horizontal res 2, bit 7 reverse.
///
/// Converting a game means finding those display-mode command words in its
/// executable or image and flipping bit 3. This catches the common case where the
/// command is present as a literal; a game that computes the mode dynamically, or
/// needs frame-rate/speed compensation, needs per-game work this does not attempt.
/// </summary>
public static class Ps1VideoMode
{
    private const uint DisplayModeOpcode = 0x08;
    private const byte VideoModeBit = 0x08;   // bit 3 of the GP1(08h) parameter

    /// <summary>True if <paramref name="word"/> is a GP1(08h) display-mode command:
    /// opcode 0x08 in the top byte and the parameter confined to the low byte
    /// (bytes 1-2 zero), which is how the original hardware encodes it.</summary>
    public static bool IsDisplayModeCommand(uint word) =>
        (word & 0xFFFFFF00u) == (DisplayModeOpcode << 24);

    public static PsxVideoMode ModeOfParam(byte param) =>
        (param & VideoModeBit) != 0 ? PsxVideoMode.Pal : PsxVideoMode.Ntsc;

    public static byte SetParamMode(byte param, PsxVideoMode mode) => mode == PsxVideoMode.Pal
        ? (byte)(param | VideoModeBit)
        : (byte)(param & ~VideoModeBit);
}

/// <summary>One display-mode command found in a binary, and the byte change that
/// would convert it.</summary>
public sealed record VideoModeSite
{
    public required long Offset { get; init; }
    public required byte OldParam { get; init; }
    public required byte NewParam { get; init; }
    public PsxVideoMode CurrentMode => Ps1VideoMode.ModeOfParam(OldParam);
}

/// <summary>
/// Scans a PS-EXE or disc image for GPU display-mode commands and converts their
/// video-mode bit — producing either an in-place patch or a PPF (through the
/// existing <see cref="PpfPatch"/> engine, so the result is a standard, undoable,
/// validated patch any PPF tool can apply).
/// </summary>
public static class Ps1VideoModePatcher
{
    /// <summary>Find every display-mode command whose video mode differs from
    /// <paramref name="target"/> — i.e. the sites a conversion would change. The
    /// command word is scanned at every byte offset (unaligned included), since a
    /// literal can sit anywhere in the data.</summary>
    public static IReadOnlyList<VideoModeSite> FindSites(byte[] data, PsxVideoMode target)
    {
        ArgumentNullException.ThrowIfNull(data);
        var sites = new List<VideoModeSite>();
        for (int i = 0; i + 4 <= data.Length; i++)
        {
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i, 4));
            if (!Ps1VideoMode.IsDisplayModeCommand(word)) continue;

            byte param = (byte)(word & 0xFF);
            if (Ps1VideoMode.ModeOfParam(param) == target) continue;   // already the wanted mode

            byte newParam = Ps1VideoMode.SetParamMode(param, target);
            if (newParam == param) continue;
            sites.Add(new VideoModeSite { Offset = i, OldParam = param, NewParam = newParam });
        }
        return sites;
    }

    /// <summary>Flip the video-mode bit at every matching site in place; returns
    /// the number of sites changed.</summary>
    public static int PatchInPlace(byte[] data, PsxVideoMode target)
    {
        var sites = FindSites(data, target);
        foreach (var s in sites) data[s.Offset] = s.NewParam;
        return sites.Count;
    }

    /// <summary>Build a PPF 3.0 that performs the conversion on
    /// <paramref name="original"/>. Returns null (no patch) if there is nothing to
    /// change. The PPF is undoable and validated, like any DiscForge-made patch.</summary>
    public static byte[]? CreatePpf(byte[] original, PsxVideoMode target, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        var modified = (byte[])original.Clone();
        int changed = PatchInPlace(modified, target);
        if (changed == 0) return null;

        return PpfPatch.Create(original, modified, new PpfPatch.CreateOptions
        {
            Description = description ?? $"DiscForge {target.ToString().ToUpperInvariant()} video-mode patch",
        });
    }
}
