// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.PlayStation;

/// <summary>A PlayStation 1 memory-card container format.</summary>
public enum Ps1CardFormat
{
    /// <summary>Raw 128 KB card image (.mcr / .bin / .mcd / .mem / .vm1 / .srm / .ps).</summary>
    Raw,
    /// <summary>DexDrive .gme — a 3904-byte header then the raw card.</summary>
    DexDrive,
    /// <summary>Connectix Virtual Game Station .mem/.vgs — a 64-byte header then the raw card.</summary>
    Vgs,
    /// <summary>Not a recognised PS1 memory-card container.</summary>
    Unknown,
}

/// <summary>
/// Converts a PlayStation 1 memory card between the container formats emulators and
/// save tools use: the raw 128 KB image (.mcr and friends), the DexDrive <c>.gme</c>
/// (a 3904-byte header + the card), and the Connectix VGS format (a 64-byte header +
/// the card). This is a container transform only — the 128 KB of card data is
/// preserved byte-for-byte; nothing inside the saves is decrypted or altered.
/// </summary>
public static class Ps1CardConvert
{
    /// <summary>Raw card size: 128 KB.</summary>
    public const int CardSize = 128 * 1024;   // 131072

    private const int DexHeaderSize = 3904;
    private const int VgsHeaderSize = 64;

    private static readonly byte[] DexMagic = Encoding.ASCII.GetBytes("123-456-STD");
    private static readonly byte[] VgsMagic = Encoding.ASCII.GetBytes("VgsM");

    public sealed class Ps1CardFormatException(string message) : Exception(message);

    /// <summary>Identify the container format of a PS1 memory-card file.</summary>
    public static Ps1CardFormat Detect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (StartsWith(data, DexMagic) && data.Length >= DexHeaderSize + CardSize) return Ps1CardFormat.DexDrive;
        if (StartsWith(data, VgsMagic) && data.Length >= VgsHeaderSize + CardSize) return Ps1CardFormat.Vgs;
        // A raw card is exactly one image, or a whole number of them (some tools pad).
        if (data.Length >= CardSize && StartsWith(data.AsSpan(0, Math.Min(2, data.Length)), new byte[] { (byte)'M', (byte)'C' }))
            return Ps1CardFormat.Raw;
        if (data.Length == CardSize) return Ps1CardFormat.Raw;
        return Ps1CardFormat.Unknown;
    }

    /// <summary>Extract the raw 128 KB card from any recognised container.</summary>
    public static byte[] ToRaw(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        int offset = Detect(data) switch
        {
            Ps1CardFormat.DexDrive => DexHeaderSize,
            Ps1CardFormat.Vgs => VgsHeaderSize,
            Ps1CardFormat.Raw => 0,
            _ => throw new Ps1CardFormatException("Not a recognised PS1 memory-card container (raw / DexDrive / VGS)."),
        };
        if (offset + CardSize > data.Length)
            throw new Ps1CardFormatException("File is too short to contain a 128 KB card after its header.");
        return data.AsSpan(offset, CardSize).ToArray();
    }

    /// <summary>Convert any recognised container to the target format.</summary>
    public static byte[] Convert(byte[] data, Ps1CardFormat target)
    {
        byte[] raw = ToRaw(data);
        return target switch
        {
            Ps1CardFormat.Raw => raw,
            Ps1CardFormat.DexDrive => WrapDexDrive(raw),
            Ps1CardFormat.Vgs => WrapVgs(raw),
            _ => throw new Ps1CardFormatException($"Cannot write the format {target}."),
        };
    }

    private static byte[] WrapDexDrive(byte[] raw)
    {
        var outp = new byte[DexHeaderSize + CardSize];
        DexMagic.CopyTo(outp, 0);
        // A minimal, valid DexDrive header: the signature then a zeroed comment/flag
        // area. The 128 KB card that follows is what emulators actually read.
        raw.CopyTo(outp, DexHeaderSize);
        return outp;
    }

    private static byte[] WrapVgs(byte[] raw)
    {
        var outp = new byte[VgsHeaderSize + CardSize];
        VgsMagic.CopyTo(outp, 0);
        outp[4] = 0x01; outp[8] = 0x01;   // version / type fields VGS writes
        raw.CopyTo(outp, VgsHeaderSize);
        return outp;
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix) =>
        data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);
}
