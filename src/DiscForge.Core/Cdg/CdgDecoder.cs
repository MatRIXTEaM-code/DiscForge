// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Cdg;

/// <summary>
/// A CD+G (CD+Graphics) decoder. CD+G is the karaoke picture carried in the
/// R–W sub-channel of an audio CD: a stream of 24-byte packets, 300 packets
/// per second (four per 1/75-second sector). Only the low 6 bits of every
/// byte carry meaning — they were the 6-bit R–W symbols.
///
/// A packet is a graphics command only when <c>(command &amp; 0x3F) == 0x09</c>
/// (TV_GRAPHICS); everything else is an audio-only no-op. The instruction
/// <c>(instruction &amp; 0x3F)</c> selects the operation. This decoder maintains
/// a 300×216 four-bit indexed framebuffer and a 16-entry colour look-up table
/// (CLUT), exactly as a karaoke player would, and can render the current
/// picture to 8-bit RGBA.
///
/// Layout derived from the public "CD+G Revealed" format description; no
/// third-party source was consulted.
///
/// Implemented instructions: 1 MEMORY_PRESET, 2 BORDER_PRESET, 6 TILE_BLOCK,
/// 38 TILE_BLOCK_XOR, 30 LOAD_COLOR_TABLE_LOW, 31 LOAD_COLOR_TABLE_HIGH, and
/// 28 DEFINE_TRANSPARENT (stored, rendered opaque). SCROLL_PRESET (20) and
/// SCROLL_COPY (24) are accepted as no-ops.
/// </summary>
public sealed class CdgDecoder
{
    public const int Width = 300;
    public const int Height = 216;
    public const int PacketSize = 24;
    public const int PacketsPerSector = 4;
    public const int PacketsPerSecond = 300;

    // Instruction opcodes (low 6 bits of the instruction byte).
    private const int MemoryPreset = 1;
    private const int BorderPreset = 2;
    private const int TileBlock = 6;
    private const int ScrollPreset = 20;
    private const int ScrollCopy = 24;
    private const int DefineTransparent = 28;
    private const int LoadClutLow = 30;
    private const int LoadClutHigh = 31;
    private const int TileBlockXor = 38;

    /// <summary>Palette indices (0..15), row-major, Width × Height.</summary>
    public byte[] Screen { get; } = new byte[Width * Height];

    /// <summary>16 RGB CLUT entries, 4 bits per channel scaled to 8.</summary>
    public (byte R, byte G, byte B)[] Clut { get; } = new (byte, byte, byte)[16];

    /// <summary>Border colour index set by BORDER_PRESET (default 0).</summary>
    public int BorderColor { get; private set; }

    /// <summary>Transparent colour index set by DEFINE_TRANSPARENT, or -1
    /// when none has been defined. Stored only — rendering is opaque.</summary>
    public int TransparentColor { get; private set; } = -1;

    /// <summary>Packets fed through <see cref="ApplyPacket"/> so far.</summary>
    public int PacketsSeen { get; private set; }

    /// <summary>How many packets a positional replay
    /// (<see cref="ApplyThrough"/>/<see cref="ApplyAtTime"/>) has applied.</summary>
    public int AppliedPackets => _pos;

    private readonly IReadOnlyList<byte[]>? _packets;
    private int _pos;

    /// <summary>A stateless decoder — feed it packets with
    /// <see cref="ApplyPacket"/>.</summary>
    public CdgDecoder() { }

    /// <summary>A decoder bound to a packet stream, so it can replay up to a
    /// packet index or a playback time.</summary>
    public CdgDecoder(IReadOnlyList<byte[]> packets)
    {
        _packets = packets ?? throw new ArgumentNullException(nameof(packets));
    }

    /// <summary>Apply one 24-byte CD+G packet to the framebuffer/CLUT. A packet
    /// that is not a TV_GRAPHICS command, or one that is too short, is ignored.</summary>
    public void ApplyPacket(ReadOnlySpan<byte> packet24)
    {
        PacketsSeen++;
        if (packet24.Length < PacketSize) return;
        if ((packet24[0] & 0x3F) != 0x09) return;          // not TV graphics

        int instruction = packet24[1] & 0x3F;
        ReadOnlySpan<byte> data = packet24.Slice(4, 16);   // 16 data symbols

        switch (instruction)
        {
            case MemoryPreset:
                Array.Fill(Screen, (byte)(data[0] & 0x0F));
                break;

            case BorderPreset:
                BorderColor = data[0] & 0x0F;
                break;

            case TileBlock:
            case TileBlockXor:
                DrawTile(data, xor: instruction == TileBlockXor);
                break;

            case LoadClutLow:
                LoadClut(data, baseIndex: 0);
                break;

            case LoadClutHigh:
                LoadClut(data, baseIndex: 8);
                break;

            case DefineTransparent:
                TransparentColor = data[0] & 0x0F;
                break;

            case ScrollPreset:
            case ScrollCopy:
            default:
                // Scrolling is treated as a no-op; unknown instructions ignored.
                break;
        }
    }

    /// <summary>Replay the bound packet stream so that exactly
    /// <paramref name="packetIndex"/> packets have been applied (indices
    /// 0..packetIndex-1). Idempotent when called with a non-decreasing index.</summary>
    public void ApplyThrough(int packetIndex)
    {
        if (_packets is null)
            throw new InvalidOperationException("This decoder was not constructed with a packet stream.");
        if (packetIndex > _packets.Count) packetIndex = _packets.Count;
        for (; _pos < packetIndex; _pos++)
            ApplyPacket(_packets[_pos]);
    }

    /// <summary>Replay the bound packet stream up to a playback time. The packet
    /// index is <c>floor(seconds × 300)</c>, so one second applies 300 packets.</summary>
    public void ApplyAtTime(TimeSpan time)
    {
        double seconds = time.TotalSeconds;
        if (seconds < 0) seconds = 0;
        long index = (long)Math.Floor(seconds * PacketsPerSecond);
        ApplyThrough(index > int.MaxValue ? int.MaxValue : (int)index);
    }

    /// <summary>Render the current framebuffer through the CLUT to 8-bit RGBA
    /// (Width × Height × 4 bytes, top-left origin, fully opaque).</summary>
    public byte[] RenderRgba()
    {
        var rgba = new byte[Width * Height * 4];
        for (int i = 0; i < Screen.Length; i++)
        {
            var (r, g, b) = Clut[Screen[i] & 0x0F];
            int o = i * 4;
            rgba[o] = r;
            rgba[o + 1] = g;
            rgba[o + 2] = b;
            rgba[o + 3] = 0xFF;
        }
        return rgba;
    }

    /// <summary>Render the current picture into a <see cref="CdgImage"/>.</summary>
    public CdgImage Render() => new(Width, Height, RenderRgba());

    private void DrawTile(ReadOnlySpan<byte> data, bool xor)
    {
        byte c0 = (byte)(data[0] & 0x0F);
        byte c1 = (byte)(data[1] & 0x0F);
        int row = data[2] & 0x1F;      // ×12 px, 0..17
        int col = data[3] & 0x3F;      // ×6 px, 0..49
        if (row >= Height / 12 || col >= Width / 6) return;

        int baseY = row * 12;
        int baseX = col * 6;
        for (int y = 0; y < 12; y++)
        {
            int bits = data[4 + y] & 0x3F;
            int rowOffset = (baseY + y) * Width + baseX;
            for (int x = 0; x < 6; x++)
            {
                byte pix = ((bits >> (5 - x)) & 1) != 0 ? c1 : c0;
                int idx = rowOffset + x;
                Screen[idx] = xor ? (byte)((Screen[idx] ^ pix) & 0x0F) : pix;
            }
        }
    }

    private void LoadClut(ReadOnlySpan<byte> data, int baseIndex)
    {
        for (int i = 0; i < 8; i++)
        {
            int hi = data[i * 2] & 0x3F;
            int lo = data[i * 2 + 1] & 0x3F;
            int red = (hi >> 2) & 0x0F;
            int green = ((hi & 0x03) << 2) | ((lo >> 4) & 0x03);
            int blue = lo & 0x0F;
            Clut[baseIndex + i] = (Scale(red), Scale(green), Scale(blue));
        }
    }

    /// <summary>Scale a 4-bit channel to 8 bits (value × 17 = (v &lt;&lt; 4) | v).</summary>
    private static byte Scale(int v) => (byte)((v << 4) | v);
}
