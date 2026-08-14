// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;

namespace DiscForge.Core.Cdi;

/// <summary>
/// Parser for DiscJuggler CDI images.
///
/// Status:
///  - Trailer + descriptor location: implemented and unit-tested. (✅ per spec)
///  - Descriptor walk (sessions/tracks): implemented against docs/CDI_FORMAT.md §3–4,
///    but several field skips are marked ⚠️ in the spec and MUST be validated
///    against real v2/v3/v3.5 images before this parser is considered trustworthy.
///    Until then, ParseDescriptor throws <see cref="CdiFormatException"/> with a
///    descriptive message when it detects a structural inconsistency, rather than
///    returning silently-wrong data.
/// </summary>
public static class CdiParser
{
    public const int TrailerLength = 8;

    /// <summary>Minimum plausible CDI: trailer + a descriptor of some size.</summary>
    public const int MinimumFileLength = TrailerLength + 2;

    /// <summary>
    /// Reads the 8-byte trailer and resolves the descriptor's absolute offset.
    /// Pure function over the stream tail; safe on any seekable stream.
    /// </summary>
    public static CdiTrailer ReadTrailer(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));
        if (stream.Length < MinimumFileLength)
            throw new CdiFormatException($"File too small ({stream.Length} bytes) to be a CDI image.");

        Span<byte> tail = stackalloc byte[TrailerLength];
        stream.Seek(-TrailerLength, SeekOrigin.End);
        stream.ReadExactly(tail);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(tail[..4]);
        uint locator = BinaryPrimitives.ReadUInt32LittleEndian(tail[4..]);

        var version = magic switch
        {
            (uint)CdiVersion.V2 => CdiVersion.V2,
            (uint)CdiVersion.V3 => CdiVersion.V3,
            (uint)CdiVersion.V35 => CdiVersion.V35,
            _ => CdiVersion.Unknown,
        };

        if (version == CdiVersion.Unknown)
            throw new CdiFormatException(
                $"Not a CDI image: unknown version magic 0x{magic:X8} at EOF-8." +
                (magic == 0
                    // A CDI's trailer is written last, so an all-zero magic almost
                    // always means the writer never finished — a rip or conversion
                    // that failed part-way, not a foreign file format.
                    ? " The magic is all zeros, which usually means the file is a truncated or " +
                      "incomplete image — the trailer is written last, so a read or conversion " +
                      "that failed part-way leaves exactly this. The data before it may still be " +
                      "intact, but the image cannot be opened."
                    : ""));

        // v2/v3: locator is an absolute offset of the descriptor.
        // v3.5+: locator is the descriptor LENGTH; descriptor starts at EOF - length.
        long descriptorOffset = version == CdiVersion.V35
            ? stream.Length - locator
            : locator;

        if (descriptorOffset < 0 || descriptorOffset >= stream.Length - TrailerLength)
            throw new CdiFormatException(
                $"Descriptor offset {descriptorOffset} is outside the file " +
                $"(length {stream.Length}). Truncated or corrupt image?");

        return new CdiTrailer(version, locator, descriptorOffset);
    }

    /// <summary>
    /// Full parse: trailer, then descriptor walk.
    /// </summary>
    public static CdiImage Parse(Stream stream)
    {
        var trailer = ReadTrailer(stream);

        if (trailer.DescriptorOffset < 0 || trailer.DescriptorOffset > stream.Length)
            throw new CdiFormatException("Descriptor offset lies outside the file.");
        long descriptorLength = stream.Length - trailer.DescriptorOffset;
        if (descriptorLength > int.MaxValue)
            throw new CdiFormatException("Descriptor implausibly large.");

        var buf = new byte[descriptorLength];
        stream.Seek(trailer.DescriptorOffset, SeekOrigin.Begin);
        stream.ReadExactly(buf);

        return ParseDescriptor(buf, trailer, stream.Length);
    }

    /// <summary>
    /// Walks the descriptor per docs/CDI_FORMAT.md §3–4.
    /// ⚠️ VALIDATION PENDING: field skips marked in the spec as unverified are
    /// implemented per the public documentation but not yet confirmed against
    /// a corpus of real images. See CDI_FORMAT.md §7 for the validation plan.
    /// </summary>
    internal static CdiImage ParseDescriptor(ReadOnlySpan<byte> d, CdiTrailer trailer, long fileLength)
    {
        var r = new SpanReader(d);

        ushort nSessions = r.U16();
        if (nSessions is 0 or > 99)
            throw new CdiFormatException($"Implausible session count {nSessions}.");

        var sessions = new List<CdiSession>(nSessions);
        long runningFileOffset = 0;
        int discTrackNumber = 0;

        for (int s = 0; s < nSessions; s++)
        {
            ushort nTracks = r.U16();
            if (nTracks > 99)
                throw new CdiFormatException($"Implausible track count {nTracks} in session {s}.");

            var tracks = new List<CdiTrack>(nTracks);

            for (int t = 0; t < nTracks; t++)
            {
                var raw = ReadTrackBlock(ref r, trailer.Version);

                var sectorSize = raw.SectorSizeCode switch
                {
                    0 => CdiSectorSize.S2048,
                    1 => CdiSectorSize.S2336,
                    2 => CdiSectorSize.S2352,
                    _ => throw new CdiFormatException(
                        $"Unknown sector size code {raw.SectorSizeCode} (session {s}, track {t})."),
                };

                var mode = raw.Mode switch
                {
                    0 => CdiTrackMode.Audio,
                    1 => CdiTrackMode.Mode1,
                    2 => CdiTrackMode.Mode2,
                    _ => throw new CdiFormatException(
                        $"Unknown track mode {raw.Mode} (session {s}, track {t})."),
                };

                discTrackNumber++;
                tracks.Add(new CdiTrack
                {
                    Number = discTrackNumber,
                    SessionIndex = s,
                    Mode = mode,
                    SectorSize = sectorSize,
                    PregapSectors = raw.PregapSectors,
                    LengthSectors = raw.LengthSectors,
                    StartLba = raw.StartLba,
                    TotalSectors = raw.TotalSectors,
                    FileOffset = runningFileOffset,
                    SourceFilename = raw.Filename,
                });

                runningFileOffset += (long)raw.TotalSectors * (int)sectorSize;
            }

            // ⚠️ Session tail skip — version dependent, see CDI_FORMAT.md §3.
            r.Skip(SessionTailLength(trailer.Version));

            sessions.Add(new CdiSession { Index = s, Tracks = tracks });
        }

        if (runningFileOffset > trailer.DescriptorOffset)
            throw new CdiFormatException(
                "Track data extends past descriptor start — parser/spec mismatch. " +
                "This is exactly the failure the validation corpus exists to catch; " +
                "please report this image's version and layout.");

        return new CdiImage
        {
            Version = trailer.Version,
            FileLength = fileLength,
            DescriptorOffset = trailer.DescriptorOffset,
            Sessions = sessions,
        };
    }

    /// <summary>Session tail in the canonical layout: a single u32 (0).</summary>
    private static int SessionTailLength(CdiVersion _) => 4;

    /// <summary>
    /// Reads one track block in the DiscForge CANONICAL layout
    /// (docs/CDI_FORMAT.md §"Canonical synthetic layout"). This is the format
    /// CdiWriter emits and gen_cdi.py emits; it is fully specified with no
    /// mystery skips. Parsing "wild" cdi4dc / real-DiscJuggler descriptors is
    /// a separate compat path (future work) — those use richer layouts and need
    /// a real DJ image to pin down. The trailer/version handling above is
    /// universal and confirmed against a real image regardless.
    /// </summary>
    private static RawTrack ReadTrackBlock(ref SpanReader r, CdiVersion version)
    {
        _ = version;
        r.Skip(4);                          // lead-in (0; 0x80000000 reserved for future)

        ReadOnlySpan<byte> mark = [0, 0, 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF];
        for (int i = 0; i < 2; i++)
        {
            if (!r.Bytes(10).SequenceEqual(mark))
                throw new CdiFormatException(
                    $"Track start mark not found at descriptor offset {r.Position - 10}.");
        }

        r.Skip(4);                          // reserved0
        byte fnLen = r.U8();
        string filename = r.Ascii(fnLen);

        uint pregap = r.U32();
        uint length = r.U32();
        uint mode = r.U32();
        uint startLba = r.U32();
        uint total = r.U32();
        uint sectorSizeCode = r.U32();
        r.Skip(4);                          // reserved1 (future ISRC/flags)

        if (total < pregap + length)
            throw new CdiFormatException(
                $"Track accounting mismatch: total {total} < pregap {pregap} + length {length}.");

        return new RawTrack(filename, pregap, length, mode, startLba, total, sectorSizeCode);
    }

    private readonly record struct RawTrack(
        string Filename, uint PregapSectors, uint LengthSectors,
        uint Mode, uint StartLba, uint TotalSectors, uint SectorSizeCode);
}

/// <summary>Resolved trailer: version + descriptor location.</summary>
public readonly record struct CdiTrailer(CdiVersion Version, uint RawLocator, long DescriptorOffset);

/// <summary>Thrown when a file is not a valid/parseable CDI image.</summary>
public sealed class CdiFormatException(string message) : Exception(message);

/// <summary>Minimal forward-only little-endian span reader with bounds errors
/// that name the offset, because descriptor debugging is a hex-dump sport.</summary>
internal ref struct SpanReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _d = data;
    public int Position { get; private set; }

    private ReadOnlySpan<byte> Take(int n)
    {
        if (Position + n > _d.Length)
            throw new CdiFormatException(
                $"Descriptor underrun: wanted {n} bytes at offset {Position}, " +
                $"only {_d.Length - Position} remain.");
        var s = _d.Slice(Position, n);
        Position += n;
        return s;
    }

    public byte U8() => Take(1)[0];
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public uint PeekU32()
    {
        if (Position + 4 > _d.Length) return 0;
        return BinaryPrimitives.ReadUInt32LittleEndian(_d.Slice(Position, 4));
    }
    public ReadOnlySpan<byte> Bytes(int n) => Take(n);
    public void Skip(int n) => Take(n);
    public string Ascii(int n) => System.Text.Encoding.ASCII.GetString(Take(n));
}
