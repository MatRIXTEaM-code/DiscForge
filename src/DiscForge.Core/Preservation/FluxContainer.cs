// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Util;

namespace DiscForge.Core.Preservation;

/// <summary>Thrown when a buffer is not a well-formed DiscForge flux container.</summary>
public sealed class FluxFormatException(string message) : Exception(message);

/// <summary>
/// Calibration metadata for a raw optical RF/flux capture — everything a future decoder needs to interpret
/// the signal: the sample rate and word size, channel count, the disc's nominal rotation, and the capture
/// device it came from.
/// </summary>
public sealed record FluxMetadata
{
    public required uint SampleRateHz { get; init; }
    public required int BitsPerSample { get; init; }
    public required int Channels { get; init; }
    /// <summary>Nominal disc rotation in RPM, or 0 when not recorded.</summary>
    public required uint NominalRpm { get; init; }
    public required string DeviceProfile { get; init; }
    public required string Note { get; init; }

    /// <summary>Payload length in bytes (set on read).</summary>
    public long PayloadBytes { get; init; }
    /// <summary>CRC-32 of the payload recorded in the container.</summary>
    public uint PayloadCrc32 { get; init; }

    public string Describe() =>
        $"{SampleRateHz:N0} Hz, {BitsPerSample}-bit, {Channels}ch" +
        (NominalRpm > 0 ? $", {NominalRpm} RPM" : ", RPM n/r") +
        $", {PayloadBytes:N0} bytes" +
        (DeviceProfile.Length > 0 ? $", device \"{DeviceProfile}\"" : "");
}

/// <summary>
/// The DiscForge flux container (DFFLX1) — an open format for a <i>raw optical RF/flux capture</i> plus the
/// calibration metadata needed to decode it later. This is phase one of low-level optical preservation: no PC
/// drive captures optical RF today, but the moment such a signal exists it must be stored losslessly, with its
/// sample rate, rotation and device profile, so a software demodulator can reconstruct sectors from it as
/// understanding matures — exactly how flux tooling for floppy disks grew up around a container before the
/// decoders did. The layout is magic + a length-prefixed header (forward-compatible) + the raw payload, with a
/// CRC-32 over the payload. Read/write and integrity only — the EFM/CIRC demodulator is a separate, later stage.
/// </summary>
public static class FluxContainer
{
    private static readonly byte[] Magic = "DFFLX1\0\0"u8.ToArray();

    /// <summary>Serialise a flux capture: magic, a length-prefixed header, then the raw payload.</summary>
    public static byte[] Write(FluxMetadata meta, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(meta);

        var profile = Encoding.UTF8.GetBytes(meta.DeviceProfile ?? "");
        var note = Encoding.UTF8.GetBytes(meta.Note ?? "");
        uint crc = Crc32.Compute(payload);

        // Header body (BinaryWriter is little-endian).
        using var hs = new MemoryStream();
        using (var hw = new BinaryWriter(hs, Encoding.UTF8, leaveOpen: true))
        {
            hw.Write(meta.SampleRateHz);
            hw.Write((ushort)meta.BitsPerSample);
            hw.Write((ushort)meta.Channels);
            hw.Write(meta.NominalRpm);
            hw.Write((uint)profile.Length); hw.Write(profile);
            hw.Write((uint)note.Length); hw.Write(note);
            hw.Write((ulong)payload.Length);
            hw.Write(crc);
        }
        var header = hs.ToArray();

        using var ms = new MemoryStream();
        ms.Write(Magic);
        var lenBuf = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, (uint)header.Length);
        ms.Write(lenBuf);
        ms.Write(header);
        ms.Write(payload);
        return ms.ToArray();
    }

    /// <summary>Parse a flux container. Returns the metadata (with payload length/CRC) and the raw payload; verifies the CRC.</summary>
    public static (FluxMetadata Meta, byte[] Payload, bool CrcOk) Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < Magic.Length + 4 || !data[..Magic.Length].SequenceEqual(Magic))
            throw new FluxFormatException("Not a DiscForge flux container (bad DFFLX1 magic).");

        int pos = Magic.Length;
        uint headerLen = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]); pos += 4;
        if (pos + headerLen > data.Length)
            throw new FluxFormatException("Flux header runs past the end of the file.");

        var header = data.Slice(pos, (int)headerLen);
        int hp = 0;

        uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(header[hp..]); hp += 4;
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(header[hp..]); hp += 2;
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(header[hp..]); hp += 2;
        uint rpm = BinaryPrimitives.ReadUInt32LittleEndian(header[hp..]); hp += 4;

        int profLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[hp..]); hp += 4;
        string profile = Encoding.UTF8.GetString(header.Slice(hp, profLen)); hp += profLen;
        int noteLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[hp..]); hp += 4;
        string note = Encoding.UTF8.GetString(header.Slice(hp, noteLen)); hp += noteLen;

        ulong sampleBytes = BinaryPrimitives.ReadUInt64LittleEndian(header[hp..]); hp += 8;
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[hp..]); hp += 4;

        pos += (int)headerLen;
        long available = data.Length - pos;
        if ((long)sampleBytes > available)
            throw new FluxFormatException($"Flux payload is truncated: header claims {sampleBytes} bytes, {available} present.");

        var payload = data.Slice(pos, (int)sampleBytes).ToArray();
        bool crcOk = Crc32.Compute(payload) == storedCrc;

        var meta = new FluxMetadata
        {
            SampleRateHz = sampleRate,
            BitsPerSample = bits,
            Channels = channels,
            NominalRpm = rpm,
            DeviceProfile = profile,
            Note = note,
            PayloadBytes = (long)sampleBytes,
            PayloadCrc32 = storedCrc,
        };
        return (meta, payload, crcOk);
    }
}
