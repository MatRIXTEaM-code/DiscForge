// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;

namespace DiscForge.Core.GameCube;

/// <summary>A GameCube bi2.bin ("boot info 2") header — the fixed block right after boot.bin that carries
/// the disc's region and a handful of debug/dev-kit fields real retail dumps normally leave at zero.</summary>
public sealed record GcBi2
{
    /// <summary>Non-zero on a debug/dev-kit build — retail discs leave this at zero.</summary>
    public required uint DebugMonitorSize { get; init; }
    /// <summary>Non-zero on a debug/dev-kit build — retail discs leave this at zero.</summary>
    public required uint SimulatedMemorySize { get; init; }
    public required uint ArgumentOffset { get; init; }
    public required uint DebugFlag { get; init; }
    public required uint TrackLocation { get; init; }
    public required uint TrackSize { get; init; }
    /// <summary>0 = NTSC-J, 1 = NTSC-U, 2 = PAL — the field <see cref="GameCubeVerify"/> already cross-checks
    /// against the game-code region letter.</summary>
    public required byte CountryCode { get; init; }

    public string CountryName => CountryCode switch { 0 => "NTSC-J", 1 => "NTSC-U", 2 => "PAL", _ => "?" };
    /// <summary>True when either debug field is non-zero — a signal this came from a dev-kit / debug build
    /// rather than a retail pressing, worth surfacing rather than silently treating as a bad retail dump.</summary>
    public bool LooksLikeDebugBuild => DebugMonitorSize != 0 || SimulatedMemorySize != 0;
}

public static partial class GcBoot
{
    /// <summary>bi2.bin always starts right after boot.bin, at 0x440.</summary>
    public const int Bi2Offset = 0x440;
    /// <summary>bi2.bin's country-code field, relative to <see cref="Bi2Offset"/> (absolute 0x458).</summary>
    private const int Bi2CountryCodeRelOffset = 0x18;

    public static GcBi2 ReadBi2(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buf = new byte[0x20];
        stream.Seek(Bi2Offset, SeekOrigin.Begin);
        if (!ReadFull(stream, buf)) throw new GameCubeFormatException("Image is too small to hold bi2.bin.");

        uint U(int o) => BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(o));
        return new GcBi2
        {
            DebugMonitorSize = U(0x00),
            SimulatedMemorySize = U(0x04),
            ArgumentOffset = U(0x08),
            DebugFlag = U(0x0C),
            TrackLocation = U(0x10),
            TrackSize = U(0x14),
            CountryCode = buf[Bi2CountryCodeRelOffset],
        };
    }

    /// <summary>One problem found while confirming the boot chain header→apploader→DOL→FST is complete and
    /// internally consistent — as opposed to <see cref="ReadApploader"/>/<see cref="ReadDol"/> individually
    /// succeeding, which only proves each piece is independently well-formed, not that they agree with the
    /// header's own pointers or fit inside the image.</summary>
    public sealed record BootChainIssue(string Stage, string Detail);

    /// <summary>
    /// Confirm the full boot chain a GameCube disc actually needs to start: the header's own DOL/FST
    /// pointers land inside the image, bi2.bin parses, the apploader at the fixed 0x2440 offset parses and
    /// its declared size+trailer fit inside the image, and the DOL it points at parses and fits too. Read-only
    /// — this proves the chain is present and self-consistent, never boots or executes anything.
    /// </summary>
    public static IReadOnlyList<BootChainIssue> CheckChain(Stream stream, long dolOffset, long fstOffset, long fstSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var issues = new List<BootChainIssue>();
        long length = stream.Length;

        try { ReadBi2(stream); }
        catch (Exception ex) { issues.Add(new BootChainIssue("bi2.bin", ex.Message)); }

        GcApploader? apploader = null;
        try { apploader = ReadApploader(stream); }
        catch (Exception ex) { issues.Add(new BootChainIssue("apploader", ex.Message)); }
        if (apploader is { } a)
        {
            long end = ApploaderOffset + 0x20 + a.Size + a.TrailerSize;
            if (end > length)
                issues.Add(new BootChainIssue("apploader", $"declared size+trailer runs to 0x{end:X}, past the end of the image."));
        }

        if (dolOffset <= 0 || dolOffset >= length)
            issues.Add(new BootChainIssue("DOL", $"header's DOL offset 0x{dolOffset:X} is outside the image."));
        else
        {
            try
            {
                var d = ReadDol(stream, dolOffset);
                if (dolOffset + d.TotalSize > length)
                    issues.Add(new BootChainIssue("DOL", $"declared sections run to 0x{dolOffset + d.TotalSize:X}, past the end of the image."));
            }
            catch (Exception ex) { issues.Add(new BootChainIssue("DOL", ex.Message)); }
        }

        if (fstOffset <= 0 || fstOffset >= length)
            issues.Add(new BootChainIssue("FST", $"header's FST offset 0x{fstOffset:X} is outside the image."));
        else if (fstOffset + fstSize > length)
            issues.Add(new BootChainIssue("FST", $"header's FST offset+size runs to 0x{fstOffset + fstSize:X}, past the end of the image."));

        return issues;
    }
}
