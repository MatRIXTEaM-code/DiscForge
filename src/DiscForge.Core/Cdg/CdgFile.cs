// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Cdg;

/// <summary>
/// A parsed <c>.cdg</c> file: nothing more than the packet stream chunked into
/// 24-byte records. A trailing partial chunk (fewer than 24 bytes) is ignored.
/// </summary>
public sealed class CdgFile
{
    /// <summary>The 24-byte packets, in order.</summary>
    public IReadOnlyList<byte[]> Packets { get; }

    private CdgFile(List<byte[]> packets) => Packets = packets;

    /// <summary>Parse a <c>.cdg</c> byte buffer into its packets.</summary>
    public static CdgFile Parse(byte[] cdg)
    {
        ArgumentNullException.ThrowIfNull(cdg);
        return Parse((ReadOnlySpan<byte>)cdg);
    }

    /// <summary>Parse a <c>.cdg</c> buffer into its packets.</summary>
    public static CdgFile Parse(ReadOnlySpan<byte> cdg)
    {
        int count = cdg.Length / CdgDecoder.PacketSize;
        var packets = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
            packets.Add(cdg.Slice(i * CdgDecoder.PacketSize, CdgDecoder.PacketSize).ToArray());
        return new CdgFile(packets);
    }

    /// <summary>Parse a <c>.cdg</c> stream into its packets.</summary>
    public static CdgFile Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Parse(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }
}
