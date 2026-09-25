// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Preservation;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the DFFLX1 flux container: a write→read round-trip recovers the calibration metadata and the raw
/// payload byte-for-byte with a matching CRC; flipping a payload byte in the serialised container is caught by
/// the CRC; and a buffer without the magic is rejected.
/// </summary>
public class FluxContainerTests
{
    private static FluxMetadata Meta() => new()
    {
        SampleRateHz = 48_000_000,
        BitsPerSample = 8,
        Channels = 1,
        NominalRpm = 480,
        DeviceProfile = "Plextor PX-W5224TA RF tap",
        Note = "test capture",
    };

    private static byte[] Payload(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(i * 131 + 7);
        return b;
    }

    [Fact]
    public void Write_then_read_round_trips_metadata_and_payload()
    {
        var payload = Payload(20_000);
        var bytes = FluxContainer.Write(Meta(), payload);

        var (meta, got, crcOk) = FluxContainer.Read(bytes);
        Assert.True(crcOk);
        Assert.Equal(48_000_000u, meta.SampleRateHz);
        Assert.Equal(8, meta.BitsPerSample);
        Assert.Equal(1, meta.Channels);
        Assert.Equal(480u, meta.NominalRpm);
        Assert.Equal("Plextor PX-W5224TA RF tap", meta.DeviceProfile);
        Assert.Equal("test capture", meta.Note);
        Assert.Equal(payload.Length, meta.PayloadBytes);
        Assert.Equal(payload, got);
    }

    [Fact]
    public void A_flipped_payload_byte_fails_the_crc()
    {
        var bytes = FluxContainer.Write(Meta(), Payload(20_000));
        bytes[^100] ^= 0xFF;                 // corrupt a payload byte near the end

        var (_, _, crcOk) = FluxContainer.Read(bytes);
        Assert.False(crcOk);
    }

    [Fact]
    public void A_buffer_without_the_magic_is_rejected()
    {
        Assert.Throws<FluxFormatException>(() => FluxContainer.Read(new byte[64]));
    }

    [Fact]
    public void An_empty_payload_is_valid()
    {
        var bytes = FluxContainer.Write(Meta(), Array.Empty<byte>());
        var (meta, got, crcOk) = FluxContainer.Read(bytes);
        Assert.True(crcOk);
        Assert.Equal(0, meta.PayloadBytes);
        Assert.Empty(got);
    }
}
