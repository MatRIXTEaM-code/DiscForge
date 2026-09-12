// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.PlayStation;

public sealed class Ps1PsvFormatException(string message) : Exception(message);

/// <summary>One PS1 save extracted from, or ready to wrap into, a PS3/PSP <c>.psv</c> file.</summary>
public sealed record Ps1PsvSave
{
    /// <summary>The 20-byte product/save identifier from the PSV header — the same field
    /// <see cref="PsxMemoryCard"/> reads out of a card's directory entry (e.g.
    /// "BASCUS-94163FF7-S01": 2-byte country + 10-byte product code + 8-byte save id).</summary>
    public required string ProductCode { get; init; }
    /// <summary>The single 8 KB (one memory-card block) "SC"-framed save payload — byte-for-byte
    /// what <see cref="PsxMemoryCard.Extract"/> would hand back for a one-block save.</summary>
    public required byte[] SaveBlock { get; init; }
}

/// <summary>
/// Reads and writes the PS3/PSP "<c>.psv</c>" single-save export format — the file a PS3's
/// Trophy/Save Data Utility or a PSP produces when it exports one PS1 memory-card save (as
/// opposed to a whole card, which is <c>.vmp</c> — see <see cref="Ps1CardConvert"/>).
///
/// Layout (clean-room, from the publicly documented PS1 Savedata format used across the PS3
/// homebrew save-editor tooling — psdevwiki's PS1_Savedata page and corroborating third-party
/// tool documentation): magic <c>00 56 53 50 00 00 00 00</c> ("\0VSP\0\0\0\0") at offset 0x00;
/// an AES-encrypted key seed at 0x08 (20 bytes) and a SHA-1 HMAC at 0x1C (20 bytes) the console
/// uses to authenticate the file; a platform-indicator dword at 0x38 (0x14 for PS1 saves); a
/// version dword at 0x3C (0x01 for PS1); the 20-byte product/save identifier at 0x64; and the
/// single 8 KB "SC"-framed save block starting at 0x84.
///
/// Honesty note, same spirit as <see cref="Ps1CardConvert"/>'s <c>.vmp</c> support: the key-seed
/// and HMAC fields exist so a real PS3/PSP can verify the file came from that console. DiscForge
/// has no Sony signing key material and will not fabricate a signature, so <see cref="ToPsv"/>
/// writes those fields as zero. The result is byte-correct everywhere else — the product code and
/// the save payload are exactly what a genuine export carries — but it will not pass a real
/// console's authenticity check. It's meant for interchange with tools that read the save data
/// directly (memory-card managers, emulators), not for injecting into a real PS3/PSP's save list.
/// </summary>
public static class Ps1SingleSave
{
    private const int HeaderSize = 0x84;
    private const int SaveBlockSize = 8192;   // one memory-card block — matches PsxMemoryCard.BlockSize
    private const int ProductCodeOffset = 0x64;
    private const int ProductCodeLength = 20;
    private const int PlatformOffset = 0x38;
    private const uint PlatformPs1 = 0x14;

    private static readonly byte[] PsvMagic = { 0x00, (byte)'V', (byte)'S', (byte)'P', 0x00, 0x00, 0x00, 0x00 };

    /// <summary>True when <paramref name="data"/> opens with the .psv magic and is long enough
    /// to hold a full header + one save block.</summary>
    public static bool IsPsv(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.Length >= HeaderSize + SaveBlockSize
            && data.AsSpan(0, PsvMagic.Length).SequenceEqual(PsvMagic);
    }

    /// <summary>Parse a .psv file: the product code and the raw 8 KB save block. Does not
    /// verify (and cannot verify, lacking Sony key material) the file's HMAC signature.</summary>
    public static Ps1PsvSave Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!IsPsv(data))
            throw new Ps1PsvFormatException("Missing the .psv magic (\"\\0VSP\\0\\0\\0\\0\") or file too short.");

        uint platform = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(PlatformOffset, 4));
        if (platform != PlatformPs1)
            throw new Ps1PsvFormatException(
                $"Platform indicator 0x{platform:X2} is not PS1 (0x14) — this .psv holds a save for a different system.");

        string code = ReadProductCode(data);
        byte[] block = data.AsSpan(HeaderSize, SaveBlockSize).ToArray();
        return new Ps1PsvSave { ProductCode = code, SaveBlock = block };
    }

    /// <summary>
    /// Wrap a single 8 KB save block (as extracted by <see cref="PsxMemoryCard.Extract"/> for a
    /// one-block save) into a .psv container. The key-seed/HMAC signature fields are written as
    /// zero — see the class doc-comment: this file will not authenticate on a real PS3/PSP.
    /// </summary>
    public static byte[] ToPsv(string productCode, byte[] saveBlock)
    {
        ArgumentNullException.ThrowIfNull(productCode);
        ArgumentNullException.ThrowIfNull(saveBlock);
        if (saveBlock.Length != SaveBlockSize)
            throw new Ps1PsvFormatException(
                $".psv carries exactly one {SaveBlockSize:N0}-byte memory-card block; got {saveBlock.Length:N0}. " +
                "Multi-block saves aren't representable in this single-save format.");

        var outp = new byte[HeaderSize + SaveBlockSize];
        PsvMagic.CopyTo(outp, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(PlatformOffset, 4), PlatformPs1);
        BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(0x3C, 4), 0x01);   // version: PS1

        var codeBytes = Encoding.ASCII.GetBytes(productCode);
        int n = Math.Min(codeBytes.Length, ProductCodeLength);
        codeBytes.AsSpan(0, n).CopyTo(outp.AsSpan(ProductCodeOffset, n));

        saveBlock.CopyTo(outp, HeaderSize);
        return outp;
    }

    private static string ReadProductCode(byte[] data)
    {
        int at = ProductCodeOffset;
        int len = 0;
        while (len < ProductCodeLength && data[at + len] != 0) len++;
        return Encoding.ASCII.GetString(data, at, len);
    }
}
