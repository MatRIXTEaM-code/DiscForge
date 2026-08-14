// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.GameCube;

/// <summary>The size class a GameCube/Wii image's byte length places it in.</summary>
public enum GcSizeClass { GameCubeSingleLayer, GameCubeSmaller, Larger, Unknown }

/// <summary>A single-image "good dump" health verdict for a GameCube disc — the check Redump's guide can't
/// make from one file (its only test is dumping twice and comparing hashes).</summary>
public sealed record GameCubeHealth
{
    public required string GameCode { get; init; }
    public required string MakerCode { get; init; }
    public required string GameName { get; init; }
    public required int DiscNumber { get; init; }
    public required int Version { get; init; }
    public required bool AudioStreaming { get; init; }
    /// <summary>Region from the bi2.bin country code (byte 0x458).</summary>
    public required string BiRegion { get; init; }
    /// <summary>Region implied by the 4th character of the game code (E=USA, P=Europe, J=Japan…).</summary>
    public required string CodeRegion { get; init; }
    public bool RegionConsistent => BiRegion == CodeRegion || CodeRegion == "?" || BiRegion == "?";

    public required long DiscSize { get; init; }
    public required long DolOffset { get; init; }
    public required long FstOffset { get; init; }
    public required long FstSize { get; init; }
    public required GcSizeClass SizeClass { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }
    public bool Healthy => Warnings.Count == 0;

    public string Summary()
    {
        string cls = SizeClass switch
        {
            GcSizeClass.GameCubeSingleLayer => "standard GameCube single-layer size",
            GcSizeClass.GameCubeSmaller => "SMALLER than a standard GameCube disc (scrubbed/trimmed or truncated?)",
            GcSizeClass.Larger => "larger than a GameCube single-layer (dual-layer or Wii?)",
            _ => "unknown size class",
        };
        var sb = new StringBuilder(
            $"{GameCode} \"{GameName}\" v1.{Version:00} ({BiRegion}) — {DiscSize:N0} bytes, {cls}. ");
        sb.Append(Healthy ? "Boot structure sane; dump looks healthy."
                          : $"{Warnings.Count} concern(s): {string.Join("; ", Warnings)}.");
        return sb.ToString();
    }
}

/// <summary>
/// gc-verify — a single-image health check for a GameCube disc dump. Reusing the boot header and file
/// table DiscForge already reads, it verifies the DVD magic, that the DOL and FST offsets/sizes fall
/// within the image, the boot chain is present, the region agrees between the bi2 country code and the
/// game-code region letter, and that the byte length matches a standard GameCube single-layer disc (so a
/// scrubbed/trimmed/truncated dump is flagged from one file, without a second dump to compare against).
/// Identification/verification only.
/// </summary>
public static class GameCubeVerify
{
    /// <summary>The exact byte length of a standard GameCube single-layer disc image.</summary>
    public const long GameCubeSingleLayerBytes = 1_459_978_240;   // 0x57058000

    public static GameCubeHealth Check(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var disc = GcmReader.Read(stream);       // validates the 0xC2339F3D magic
        long size = stream.Length;

        // Re-read the few header bytes GcmReader doesn't surface: audio-stream flag, DOL/FST fields, region.
        byte audioFlag = ReadByte(stream, 0x08);
        long dol = ReadU32(stream, 0x420);
        long fstOff = ReadU32(stream, 0x424);
        long fstSize = ReadU32(stream, 0x428);
        byte country = ReadByte(stream, 0x458);   // bi2.bin (0x440) + country-code offset (0x18)

        string biRegion = country switch { 0 => "NTSC-J", 1 => "NTSC-U", 2 => "PAL", _ => "?" };
        string codeRegion = disc.GameCode.Length >= 4
            ? disc.GameCode[3] switch { 'J' => "NTSC-J", 'E' => "NTSC-U", 'U' => "NTSC-U",
                                        'P' or 'D' or 'F' or 'S' or 'I' or 'H' or 'X' or 'Y' => "PAL", _ => "?" }
            : "?";

        var warnings = new List<string>();
        if (dol == 0 || dol >= size) warnings.Add($"DOL offset 0x{dol:X} is outside the image");
        if (fstOff == 0 || fstOff >= size) warnings.Add($"FST offset 0x{fstOff:X} is outside the image");
        if (fstOff + fstSize > size) warnings.Add("FST extends past the end of the image (truncated?)");
        if (disc.Entries.Count == 0) warnings.Add("the file table is empty");

        GcSizeClass cls =
            size == GameCubeSingleLayerBytes ? GcSizeClass.GameCubeSingleLayer :
            size < GameCubeSingleLayerBytes ? GcSizeClass.GameCubeSmaller :
            GcSizeClass.Larger;
        if (cls == GcSizeClass.GameCubeSmaller)
            warnings.Add($"image is {GameCubeSingleLayerBytes - size:N0} bytes short of a standard disc — " +
                         "likely scrubbed/trimmed (recoverable) or truncated");
        if (!(biRegion == codeRegion || codeRegion == "?" || biRegion == "?"))
            warnings.Add($"region mismatch: bi2 says {biRegion} but the game code implies {codeRegion}");

        return new GameCubeHealth
        {
            GameCode = disc.GameCode,
            MakerCode = disc.MakerCode,
            GameName = disc.GameName,
            DiscNumber = disc.DiscId,
            Version = disc.Version,
            AudioStreaming = audioFlag != 0,
            BiRegion = biRegion,
            CodeRegion = codeRegion,
            DiscSize = size,
            DolOffset = dol,
            FstOffset = fstOff,
            FstSize = fstSize,
            SizeClass = cls,
            Warnings = warnings,
        };
    }

    public static GameCubeHealth CheckFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var fs = File.OpenRead(path);
        return Check(fs);
    }

    private static byte ReadByte(Stream s, long at)
    {
        s.Seek(at, SeekOrigin.Begin);
        int b = s.ReadByte();
        return b < 0 ? (byte)0 : (byte)b;
    }

    private static long ReadU32(Stream s, long at)
    {
        s.Seek(at, SeekOrigin.Begin);
        var b = new byte[4];
        return s.Read(b, 0, 4) == 4 ? BinaryPrimitives.ReadUInt32BigEndian(b) : 0;
    }
}
