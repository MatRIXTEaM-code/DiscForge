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
    /// <summary>PS3/PSP "virtual memory card" .vmp — a 128-byte (0x80) header then the raw card.
    /// See the doc-comment on <see cref="Ps1CardConvert"/> for what this container does and does
    /// not carry.</summary>
    Vmp,
    /// <summary>Not a recognised PS1 memory-card container.</summary>
    Unknown,
}

/// <summary>
/// Converts a PlayStation 1 memory card between the container formats emulators and
/// save tools use: the raw 128 KB image (.mcr and friends), the DexDrive <c>.gme</c>
/// (a 3904-byte header + the card), the Connectix VGS format (a 64-byte header +
/// the card), and the PS3/PSP "virtual memory card" <c>.vmp</c> (a 128-byte header +
/// the card). This is a container transform only — the 128 KB of card data is
/// preserved byte-for-byte; nothing inside the saves is decrypted or altered.
///
/// <c>.vmp</c> honesty note: on a real PS3/PSP, that 128-byte header carries an
/// AES-encrypted key seed and a SHA-1 HMAC the console uses to authenticate the file —
/// material derived from Sony's per-console signing keys, which DiscForge does not have
/// and will not attempt to reproduce. <see cref="Detect"/> therefore identifies a
/// <c>.vmp</c> STRUCTURALLY (the documented total size of 0x20080 bytes plus the "MC"
/// card header at the known 0x80 offset) rather than by trusting an unverified magic-byte
/// sequence, and <see cref="Convert"/> writes that header as zeros. The 128 KB of card
/// data placed after it is byte-for-byte identical to every other container this class
/// produces — exactly what a real card holds — but a <c>.vmp</c> built here carries no
/// valid signature and a real PS3/PSP will refuse it; it is meant for tools (emulators,
/// other memory-card utilities) that read the card data directly. This mirrors the
/// project's "PhysicallyUncapturable" honesty pattern elsewhere: say plainly what the
/// output can and cannot do rather than silently overclaiming.
/// </summary>
public static class Ps1CardConvert
{
    /// <summary>Raw card size: 128 KB.</summary>
    public const int CardSize = 128 * 1024;   // 131072

    private const int DexHeaderSize = 3904;
    private const int VgsHeaderSize = 64;
    private const int VmpHeaderSize = 128;   // 0x80

    private static readonly byte[] DexMagic = Encoding.ASCII.GetBytes("123-456-STD");
    private static readonly byte[] VgsMagic = Encoding.ASCII.GetBytes("VgsM");

    public sealed class Ps1CardFormatException(string message) : Exception(message);

    /// <summary>Identify the container format of a PS1 memory-card file.</summary>
    public static Ps1CardFormat Detect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (StartsWith(data, DexMagic) && data.Length >= DexHeaderSize + CardSize) return Ps1CardFormat.DexDrive;
        if (StartsWith(data, VgsMagic) && data.Length >= VgsHeaderSize + CardSize) return Ps1CardFormat.Vgs;
        // .vmp: identified structurally (exact documented size + the "MC" card header sitting
        // right where the spec says the 128-byte signed header ends) — see the class doc-comment
        // for why this doesn't trust an unverified magic-byte sequence for the header itself.
        if (data.Length == VmpHeaderSize + CardSize &&
            data[VmpHeaderSize] == (byte)'M' && data[VmpHeaderSize + 1] == (byte)'C')
            return Ps1CardFormat.Vmp;
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
            Ps1CardFormat.Vmp => VmpHeaderSize,
            Ps1CardFormat.Raw => 0,
            _ => throw new Ps1CardFormatException("Not a recognised PS1 memory-card container (raw / DexDrive / VGS / VMP)."),
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
            Ps1CardFormat.Vmp => WrapVmp(raw),
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

    private static byte[] WrapVmp(byte[] raw)
    {
        // The 128-byte header is left zeroed — see the class doc-comment: DiscForge has no Sony
        // signing key material to populate the real key-seed/HMAC fields with, and won't fabricate
        // values that would masquerade as a genuine signature. What follows is the untouched card.
        var outp = new byte[VmpHeaderSize + CardSize];
        raw.CopyTo(outp, VmpHeaderSize);
        return outp;
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix) =>
        data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);
}
