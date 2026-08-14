// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Rom;

/// <summary>
/// What a cartridge ROM was identified as. A purely descriptive record built from the
/// ROM's own header/metadata region — the console signature, the title the cartridge
/// declares, its game/product code, region and a bag of platform-specific header fields
/// (<see cref="Extra"/>). Any parse concerns (a bad Nintendo-logo, a header-checksum that
/// does not recompute, a copier header that had to be skipped) are surfaced in
/// <see cref="Warnings"/> rather than thrown, so a slightly-off dump still identifies.
/// </summary>
public sealed record RomId
{
    /// <summary>The console, e.g. "Nintendo 64", "SNES", "Game Boy"; "Unknown" when nothing matched.</summary>
    public required string Platform { get; init; }

    /// <summary>The internal/product title the cartridge declares, trimmed.</summary>
    public string Title { get; init; } = "";

    /// <summary>The game/product code (e.g. N64 "NSME", GBA "AZLE"), where the format has one.</summary>
    public string GameCode { get; init; } = "";

    /// <summary>A human-readable region ("USA", "Japan", "Europe", …), where derivable.</summary>
    public string Region { get; init; } = "";

    /// <summary>Platform-specific header fields (mapper, ROM size, layout, byte order, checksums …).</summary>
    public IReadOnlyDictionary<string, string> Extra { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Non-fatal parse notes: bad logo, checksum mismatch, stripped copier header, …</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>True unless this is the sentinel <see cref="Unknown"/>.</summary>
    public bool Recognised => Platform != "Unknown";

    /// <summary>The sentinel returned for anything DiscForge does not recognise as a cartridge ROM.</summary>
    public static readonly RomId Unknown = new() { Platform = "Unknown" };
}

/// <summary>Thrown by a dedicated per-platform reader's <c>Parse</c> when the bytes it was
/// handed are not a valid header for that platform. The top-level dispatcher never lets one
/// escape — <see cref="RomIdentify.Identify"/> maps it to <see cref="RomId.Unknown"/>.</summary>
public sealed class RomFormatException(string message) : Exception(message);

/// <summary>
/// Identifies a cartridge ROM from its header and, where the format carries one, verifies its
/// checksum. Reads only documented header/metadata regions — never emulates and never touches
/// any copy protection. Dispatches across consoles by distinctive signature, most-specific
/// first; the checksum-only SNES layout is tried last so it cannot shadow a magic-bearing ROM.
/// Malformed or too-short input yields <see cref="RomId.Unknown"/> rather than throwing.
/// </summary>
public static class RomIdentify
{
    /// <summary>Identify <paramref name="rom"/>. Never throws; unrecognised input → <see cref="RomId.Unknown"/>.</summary>
    public static RomId Identify(byte[] rom)
    {
        ArgumentNullException.ThrowIfNull(rom);
        try
        {
            return
                N64Rom.TryRead(rom) ??
                NesRom.TryRead(rom) ??
                LynxRom.TryRead(rom) ??
                NeoGeoPocketRom.TryRead(rom) ??
                Atari7800Rom.TryRead(rom) ??
                GbaRom.TryRead(rom) ??
                GameBoyRom.TryRead(rom) ??
                GenesisRom.TryRead(rom) ??
                MasterSystemRom.TryRead(rom) ??
                NintendoDsRom.TryRead(rom) ??
                WonderSwanRom.TryRead(rom) ??
                SnesRom.TryRead(rom) ??
                RomId.Unknown;
        }
        catch (RomFormatException)
        {
            return RomId.Unknown;
        }
    }

    // ---- shared byte helpers (used by the per-platform readers in this folder) ----

    /// <summary>latin-1 text from a fixed field, with trailing NULs/spaces trimmed.</summary>
    internal static string Latin1(ReadOnlySpan<byte> field) =>
        Encoding.Latin1.GetString(field).TrimEnd('\0', ' ');

    /// <summary>ASCII text from a fixed field, with trailing NULs/spaces trimmed.</summary>
    internal static string Ascii(ReadOnlySpan<byte> field) =>
        Encoding.ASCII.GetString(field).TrimEnd('\0', ' ');

    internal static bool AsciiEquals(byte[] d, int at, string s)
    {
        if (at < 0 || at + s.Length > d.Length) return false;
        for (int i = 0; i < s.Length; i++) if (d[at + i] != (byte)s[i]) return false;
        return true;
    }

    internal static ushort U16Be(byte[] d, int at) => (ushort)((d[at] << 8) | d[at + 1]);
    internal static ushort U16Le(byte[] d, int at) => (ushort)(d[at] | (d[at + 1] << 8));
    internal static uint U32Be(byte[] d, int at) =>
        (uint)((d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3]);
}
