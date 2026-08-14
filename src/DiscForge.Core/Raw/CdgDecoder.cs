// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Raw;

/// <summary>
/// A CD+G graphics decoder — the semantic end of the R–W passthrough. Where
/// the generator proves the SYMBOLS survive, this proves the GRAPHICS do:
/// decode the sub-channel stream out of a generated raw image, and the same
/// picture a karaoke machine would draw must emerge.
///
/// Format (public knowledge, the "CD+G Revealed" layout): the 96 six-bit
/// symbols per sector form four 24-symbol packets. A packet whose command
/// symbol (low 6 bits) is 9 is TV graphics; the instruction symbol selects:
///   1  memory preset (clear screen to a colour)     2  border preset
///   6  tile block (6×12 two-colour tile)           38  tile block XOR
///   20 scroll preset    24 scroll copy             28  transparent colour
///   30 load colour table entries 0–7               31  entries 8–15
/// Colours are 12-bit RGB packed into two 6-bit symbols. The screen is
/// 300×216 pixels of 4-bit palette indices (visible area 294×204 inside the
/// border, but the full plane is modelled).
/// </summary>
public sealed class CdgDecoder
{
    public const int Width = 300;
    public const int Height = 216;
    public const int PacketSize = 24;
    public const int PacketsPerSector = 4;

    /// <summary>Palette indices, row-major, Width × Height.</summary>
    public byte[] Screen { get; } = new byte[Width * Height];

    /// <summary>16 RGB entries, 4 bits per channel scaled to 8.</summary>
    public (byte R, byte G, byte B)[] Palette { get; } = new (byte, byte, byte)[16];

    public int PacketsSeen { get; private set; }
    public int GraphicsPackets { get; private set; }
    public int TileCount { get; private set; }
    public int PresetCount { get; private set; }
    public int PaletteLoads { get; private set; }

    /// <summary>Feed one sector's 96 R–W symbols (values 0..63).</summary>
    public void FeedSector(ReadOnlySpan<byte> rw96)
    {
        for (int p = 0; p < PacketsPerSector; p++)
            FeedPacket(rw96.Slice(p * PacketSize, PacketSize));
    }

    /// <summary>Feed one 24-symbol packet.</summary>
    public void FeedPacket(ReadOnlySpan<byte> packet)
    {
        PacketsSeen++;
        if ((packet[0] & 0x3F) != 0x09) return;      // not TV graphics
        GraphicsPackets++;

        int instruction = packet[1] & 0x3F;
        var data = packet.Slice(4, 16);              // 16 data symbols

        switch (instruction)
        {
            case 1:                                  // memory preset
                // Repeat field: only act on repeat 0 (the rest are resends).
                if ((data[1] & 0x0F) == 0)
                {
                    byte colour = (byte)(data[0] & 0x0F);
                    Array.Fill(Screen, colour);
                    PresetCount++;
                }
                break;

            case 2:                                  // border preset
            {
                byte colour = (byte)(data[0] & 0x0F);
                for (int y = 0; y < Height; y++)
                    for (int x = 0; x < Width; x++)
                        if (x < 6 || x >= Width - 6 || y < 12 || y >= Height - 12)
                            Screen[y * Width + x] = colour;
                PresetCount++;
                break;
            }

            case 6:                                  // tile block
            case 38:                                 // tile block XOR
            {
                byte c0 = (byte)(data[0] & 0x0F);
                byte c1 = (byte)(data[1] & 0x0F);
                int row = data[2] & 0x1F;            // 0..17
                int col = data[3] & 0x3F;            // 0..49
                if (row >= Height / 12 || col >= Width / 6) break;
                bool xor = instruction == 38;
                for (int y = 0; y < 12; y++)
                {
                    int bits = data[4 + y] & 0x3F;
                    for (int x = 0; x < 6; x++)
                    {
                        byte pix = (bits & (0x20 >> x)) != 0 ? c1 : c0;
                        int idx = (row * 12 + y) * Width + col * 6 + x;
                        Screen[idx] = xor ? (byte)(Screen[idx] ^ pix) : pix;
                    }
                }
                TileCount++;
                break;
            }

            case 30:                                 // colour table 0-7
            case 31:                                 // colour table 8-15
            {
                int baseIndex = instruction == 30 ? 0 : 8;
                for (int i = 0; i < 8; i++)
                {
                    int hi = data[i * 2] & 0x3F;
                    int lo = data[i * 2 + 1] & 0x3F;
                    // 12 bits: RRRR GGGG BBBB across the two symbols.
                    int rgb = (hi << 6) | lo;
                    int r = (rgb >> 8) & 0xF, g = (rgb >> 4) & 0xF, b = rgb & 0xF;
                    Palette[baseIndex + i] = ((byte)(r * 17), (byte)(g * 17), (byte)(b * 17));
                }
                PaletteLoads++;
                break;
            }

            // Scrolling (20/24) and transparency (28) are accepted but not
            // modelled: they don't affect the correctness question the tests
            // ask, and a viewer can add them later.
            default:
                break;
        }
    }

    /// <summary>Render the screen as a binary PPM (P6) — viewable anywhere.</summary>
    public byte[] ToPpm(int scale = 2)
    {
        int w = Width * scale, h = Height * scale;
        using var ms = new MemoryStream();
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
        ms.Write(header);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = Palette[Screen[(y / scale) * Width + (x / scale)] & 0x0F];
                ms.WriteByte(r); ms.WriteByte(g); ms.WriteByte(b);
            }
        return ms.ToArray();
    }

    // ---- encoding helpers (tests and demo content) -------------------------

    /// <summary>Build a TV-graphics packet from instruction + 16 data symbols.</summary>
    public static byte[] BuildPacket(int instruction, ReadOnlySpan<byte> data16)
    {
        var p = new byte[PacketSize];
        p[0] = 0x09;
        p[1] = (byte)(instruction & 0x3F);
        for (int i = 0; i < 16; i++) p[4 + i] = (byte)(data16[i] & 0x3F);
        return p;
    }

    public static byte[] MemoryPreset(byte colour)
    {
        Span<byte> d = stackalloc byte[16];
        d[0] = (byte)(colour & 0x0F);
        d[1] = 0;                                    // repeat 0
        return BuildPacket(1, d);
    }

    public static byte[] LoadPaletteLow(ReadOnlySpan<(byte R, byte G, byte B)> entries8)
    {
        Span<byte> d = stackalloc byte[16];
        for (int i = 0; i < 8; i++)
        {
            int rgb = ((entries8[i].R / 17) << 8) | ((entries8[i].G / 17) << 4) | (entries8[i].B / 17);
            d[i * 2] = (byte)((rgb >> 6) & 0x3F);
            d[i * 2 + 1] = (byte)(rgb & 0x3F);
        }
        return BuildPacket(30, d);
    }

    public static byte[] Tile(byte c0, byte c1, int row, int col, ReadOnlySpan<byte> rows12, bool xor = false)
    {
        Span<byte> d = stackalloc byte[16];
        d[0] = c0; d[1] = c1;
        d[2] = (byte)row; d[3] = (byte)col;
        for (int i = 0; i < 12; i++) d[4 + i] = (byte)(rows12[i] & 0x3F);
        return BuildPacket(xor ? 38 : 6, d);
    }
}
