// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Rom;

/// <summary>The three N64 dump byte orders.</summary>
public enum N64ByteOrder
{
    /// <summary>Big-endian, native (.z64) — No-Intro's canonical order.</summary>
    Z64,
    /// <summary>Byte-swapped every 16-bit word (.v64).</summary>
    V64,
    /// <summary>Little-endian, every 32-bit word reversed (.n64).</summary>
    N64,
}

/// <summary>
/// Converts cartridge ROMs between the copier / interleave / byte-order representations
/// that make an otherwise-correct dump fail to match a No-Intro DAT: N64 byte order
/// (.z64 / .v64 / .n64), the SNES 512-byte copier header, the Genesis interleaved SMD
/// format, and the NES iNES header. Every operation is a lossless, reversible transform
/// of the same cartridge data — DiscForge already hashes the canonical form for
/// verification (<see cref="RomHashes"/>); this produces the actual converted file.
/// </summary>
public static class RomConvert
{
    public sealed class RomConvertException(string message) : Exception(message);

    // ---- N64 byte order ----

    /// <summary>Detect an N64 dump's byte order from its 4-byte magic, or null if unrecognised.</summary>
    public static N64ByteOrder? DetectN64Order(byte[] rom)
    {
        ArgumentNullException.ThrowIfNull(rom);
        if (rom.Length < 4) return null;
        if (rom[0] == 0x80 && rom[1] == 0x37 && rom[2] == 0x12 && rom[3] == 0x40) return N64ByteOrder.Z64;
        if (rom[0] == 0x37 && rom[1] == 0x80 && rom[2] == 0x40 && rom[3] == 0x12) return N64ByteOrder.V64;
        if (rom[0] == 0x40 && rom[1] == 0x12 && rom[2] == 0x37 && rom[3] == 0x80) return N64ByteOrder.N64;
        return null;
    }

    /// <summary>Convert an N64 dump to the requested byte order (a no-op if already there).</summary>
    public static byte[] ConvertN64(byte[] rom, N64ByteOrder target)
    {
        ArgumentNullException.ThrowIfNull(rom);
        var order = DetectN64Order(rom)
            ?? throw new RomConvertException("Not a recognised N64 dump (bad byte-order magic).");

        // Each order's transform relative to big-endian .z64 is an involution, and .z64 is
        // the fixed point: apply the source transform to reach .z64, then the target's.
        byte[] z64 = ApplyOrder((byte[])rom.Clone(), order);
        return ApplyOrder(z64, target);
    }

    private static byte[] ApplyOrder(byte[] data, N64ByteOrder order)
    {
        switch (order)
        {
            case N64ByteOrder.V64:   // swap each 16-bit word
                for (int i = 0; i + 1 < data.Length; i += 2)
                    (data[i], data[i + 1]) = (data[i + 1], data[i]);
                break;
            case N64ByteOrder.N64:   // reverse each 32-bit word
                for (int i = 0; i + 3 < data.Length; i += 4)
                {
                    (data[i], data[i + 3]) = (data[i + 3], data[i]);
                    (data[i + 1], data[i + 2]) = (data[i + 2], data[i + 1]);
                }
                break;
        }
        return data;
    }

    // ---- SNES copier header ----

    /// <summary>True when a 512-byte SMC/SWC copier header is present (len % 1024 == 512).</summary>
    public static bool HasSnesCopierHeader(byte[] rom)
    {
        ArgumentNullException.ThrowIfNull(rom);
        return SnesRom.SmcHeaderSize(rom.Length) == 512;
    }

    /// <summary>Return the ROM without its SNES copier header (unchanged when none is present).</summary>
    public static byte[] StripSnesHeader(byte[] rom)
    {
        ArgumentNullException.ThrowIfNull(rom);
        return HasSnesCopierHeader(rom) ? rom.AsSpan(512).ToArray() : (byte[])rom.Clone();
    }

    /// <summary>Return the ROM with exactly one 512-byte SNES copier header prepended.</summary>
    public static byte[] AddSnesHeader(byte[] rom)
    {
        byte[] body = StripSnesHeader(rom);           // normalise to a single header
        var outp = new byte[512 + body.Length];
        body.CopyTo(outp, 512);
        return outp;
    }

    // ---- Genesis SMD interleave ----

    /// <summary>True when the image is an interleaved SMD dump (512-byte header, 0xAA 0xBB marker).</summary>
    public static bool IsInterleavedSmd(byte[] rom)
    {
        ArgumentNullException.ThrowIfNull(rom);
        return rom.Length > 0x200 && rom[8] == 0xAA && rom[9] == 0xBB && (rom.Length - 512) % 0x4000 == 0;
    }

    /// <summary>De-interleave an SMD dump to a flat big-endian ROM (drops the 512-byte header).</summary>
    public static byte[] SmdToBin(byte[] rom)
    {
        ArgumentNullException.ThrowIfNull(rom);
        if (!IsInterleavedSmd(rom)) throw new RomConvertException("Not an interleaved Genesis SMD image.");
        int body = rom.Length - 512;
        var outp = new byte[body];
        int blocks = body / 0x4000;
        for (int b = 0; b < blocks; b++)
        {
            int src = 512 + b * 0x4000, dst = b * 0x4000;
            for (int i = 0; i < 0x2000; i++)
            {
                outp[dst + i * 2 + 1] = rom[src + i];             // odd bytes (first 8 KiB)
                outp[dst + i * 2] = rom[src + 0x2000 + i];        // even bytes (second 8 KiB)
            }
        }
        return outp;
    }

    /// <summary>Interleave a flat big-endian Genesis ROM into a 512-byte-headered SMD image.</summary>
    public static byte[] BinToSmd(byte[] rom)
    {
        ArgumentNullException.ThrowIfNull(rom);
        if (rom.Length == 0 || rom.Length % 0x4000 != 0)
            throw new RomConvertException("A flat Genesis ROM must be a whole number of 16 KiB blocks to interleave.");
        int blocks = rom.Length / 0x4000;
        var outp = new byte[512 + rom.Length];

        // SMD header: block count, the 0x03 type byte, and the 0xAA 0xBB marker.
        outp[0] = (byte)(blocks & 0xFF);
        outp[1] = 0x03;
        outp[8] = 0xAA;
        outp[9] = 0xBB;

        for (int b = 0; b < blocks; b++)
        {
            int dst = 512 + b * 0x4000, srcBlk = b * 0x4000;
            for (int i = 0; i < 0x2000; i++)
            {
                outp[dst + i] = rom[srcBlk + i * 2 + 1];          // odd bytes → first 8 KiB
                outp[dst + 0x2000 + i] = rom[srcBlk + i * 2];     // even bytes → second 8 KiB
            }
        }
        return outp;
    }

    // ---- NES iNES header ----

    /// <summary>True when the file starts with the iNES header magic "NES\x1A".</summary>
    public static bool HasInesHeader(byte[] rom)
    {
        ArgumentNullException.ThrowIfNull(rom);
        return rom.Length >= 16 && rom[0] == 0x4E && rom[1] == 0x45 && rom[2] == 0x53 && rom[3] == 0x1A;
    }

    /// <summary>
    /// Return the raw PRG/CHR data without the 16-byte iNES header (and the 512-byte
    /// trainer, if the header flags one). Adding an iNES header back needs mapper metadata
    /// and is intentionally not offered.
    /// </summary>
    public static byte[] StripInesHeader(byte[] rom)
    {
        ArgumentNullException.ThrowIfNull(rom);
        if (!HasInesHeader(rom)) throw new RomConvertException("Not an iNES-headered NES ROM.");
        int skip = 16;
        if ((rom[6] & 0x04) != 0) skip += 512;                    // trainer present
        if (skip > rom.Length) throw new RomConvertException("iNES header claims a trainer the file is too short to hold.");
        return rom.AsSpan(skip).ToArray();
    }
}
