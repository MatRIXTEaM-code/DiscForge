// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;

namespace DiscForge.Core.Psp;

/// <summary>Raised when a blob is not a well-formed PBP container.</summary>
public sealed class PbpFormatException(string message) : Exception(message);

/// <summary>One sub-file inside a PBP container.</summary>
public sealed record PbpSection
{
    /// <summary>The fixed sub-file name, e.g. "PARAM.SFO", "ICON0.PNG", "DATA.PSP".</summary>
    public required string Name { get; init; }
    /// <summary>Byte offset of this section from the start of the PBP.</summary>
    public required long Offset { get; init; }
    /// <summary>Length of this section in bytes (0 when the section is empty).</summary>
    public required long Size { get; init; }

    /// <summary>True when this section carries no data (offset equals the next section's offset).</summary>
    public bool IsEmpty => Size == 0;
}

/// <summary>
/// Parses a PBP (PlayStation Portable package, "EBOOT.PBP") container. A PBP is a
/// wrapper that concatenates up to eight fixed sub-files behind a small header of
/// little-endian offsets. This class is purely descriptive: it delimits the eight
/// sections and hands their raw bytes back. In particular DATA.PSP — which is
/// frequently an encrypted "~PSP" executable — is extracted verbatim and is never
/// decrypted or interpreted here.
///
/// Clean-room, from the public PBP container description (all little-endian):
///
///   Header:
///     0x00  4  magic     00 'P' 'B' 'P'  (00 50 42 50)
///     0x04  4  version
///     Eight u32 sub-file offsets, each from the start of the PBP, in fixed order:
///       0x08  PARAM.SFO offset
///       0x0C  ICON0.PNG offset
///       0x10  ICON1.PMF offset
///       0x14  PIC0.PNG  offset
///       0x18  PIC1.PNG  offset
///       0x1C  SND0.AT3  offset
///       0x20  DATA.PSP  offset
///       0x24  DATA.PSAR offset
///
///   Each section's length is (next section's offset) − (this section's offset);
///   the last section (DATA.PSAR) runs to the end of the file. A section is empty
///   when its offset equals the following offset. Offsets are monotonically
///   non-decreasing.
/// </summary>
public sealed class PbpFile
{
    /// <summary>The magic that opens every PBP: 00 'P' 'B' 'P'.</summary>
    private static readonly byte[] Magic = { 0x00, (byte)'P', (byte)'B', (byte)'P' };

    /// <summary>The eight sub-file names, in the order their offsets appear in the header.</summary>
    public static readonly IReadOnlyList<string> SectionNames = new[]
    {
        "PARAM.SFO", "ICON0.PNG", "ICON1.PMF", "PIC0.PNG",
        "PIC1.PNG", "SND0.AT3", "DATA.PSP", "DATA.PSAR",
    };

    private const int HeaderLength = 0x28; // 4 magic + 4 version + 8 × 4 offsets

    /// <summary>The version word from the header.</summary>
    public uint Version { get; private init; }

    /// <summary>The eight sections, always present (empty ones have Size 0), in header order.</summary>
    public IReadOnlyList<PbpSection> Sections { get; private init; } = Array.Empty<PbpSection>();

    private PbpFile() { }

    /// <summary>True when <paramref name="data"/> opens with the PBP magic.</summary>
    public static bool IsPbp(ReadOnlySpan<byte> data)
        => data.Length >= 4 && data.Slice(0, 4).SequenceEqual(Magic);

    /// <summary>Parse a PBP container from a byte buffer.</summary>
    public static PbpFile Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length < HeaderLength)
            throw new PbpFormatException(
                $"Too short to be a PBP — {data.Length} bytes, need at least the {HeaderLength}-byte header.");

        if (!IsPbp(data))
            throw new PbpFormatException(
                "Bad PBP magic — the first four bytes are not 00 'P' 'B' 'P'.");

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x04, 4));

        // Read the eight offsets, then append the file length as the terminating
        // bound so the last section's size falls out of the same subtraction.
        var offsets = new long[SectionNames.Count + 1];
        for (int i = 0; i < SectionNames.Count; i++)
            offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x08 + i * 4, 4));
        offsets[SectionNames.Count] = data.Length;

        var sections = BuildSections(offsets, data.Length);
        return new PbpFile { Version = version, Sections = sections };
    }

    /// <summary>Parse a PBP container from a stream (read to the end).</summary>
    public static PbpFile Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Parse(ms.ToArray());
    }

    private static List<PbpSection> BuildSections(long[] offsets, long fileLength)
    {
        // The first sub-file cannot start inside the header.
        if (offsets[0] < HeaderLength)
            throw new PbpFormatException(
                $"PBP first section offset {offsets[0]} lies inside the {HeaderLength}-byte header.");

        var sections = new List<PbpSection>(SectionNames.Count);
        for (int i = 0; i < SectionNames.Count; i++)
        {
            long start = offsets[i];
            long next = offsets[i + 1];

            if (start > fileLength)
                throw new PbpFormatException(
                    $"PBP section '{SectionNames[i]}' starts at {start}, past the end of the {fileLength}-byte file.");

            if (next < start)
                throw new PbpFormatException(
                    $"PBP section offsets are not monotonic — '{SectionNames[i]}' starts at {start} but the " +
                    $"following bound is {next}.");

            sections.Add(new PbpSection { Name = SectionNames[i], Offset = start, Size = next - start });
        }
        return sections;
    }

    /// <summary>The section with the given name, or throws if the name is not a PBP sub-file.</summary>
    public PbpSection GetSection(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var s in Sections)
            if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                return s;
        throw new PbpFormatException($"'{name}' is not a PBP sub-file name.");
    }

    // ---- extraction ---------------------------------------------------------

    /// <summary>Copy a named section's raw bytes out of a PBP buffer.</summary>
    public static byte[] GetSection(byte[] pbp, string name)
    {
        ArgumentNullException.ThrowIfNull(pbp);
        var file = Parse(pbp);
        var section = file.GetSection(name);
        return Slice(pbp, section);
    }

    private static byte[] Slice(byte[] pbp, PbpSection section)
    {
        if (section.Offset + section.Size > pbp.Length)
            throw new PbpFormatException(
                $"PBP section '{section.Name}' runs to {section.Offset + section.Size}, past the end of the " +
                $"{pbp.Length}-byte file.");
        var result = new byte[section.Size];
        Array.Copy(pbp, section.Offset, result, 0, section.Size);
        return result;
    }

    /// <summary>
    /// Copy one section's raw bytes from <paramref name="pbp"/> into
    /// <paramref name="output"/>. The bytes are copied verbatim — nothing is
    /// decoded or decrypted (this matters for DATA.PSP, which may be an encrypted
    /// "~PSP" executable).
    /// </summary>
    public static void ExtractSection(Stream pbp, PbpSection section, Stream output)
    {
        ArgumentNullException.ThrowIfNull(pbp);
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(output);

        if (!pbp.CanSeek)
        {
            // Fall back to buffering when the source can't seek.
            using var ms = new MemoryStream();
            pbp.CopyTo(ms);
            var buf = ms.GetBuffer();
            if (section.Offset + section.Size > ms.Length)
                throw new PbpFormatException(
                    $"PBP section '{section.Name}' runs past the end of the {ms.Length}-byte stream.");
            output.Write(buf, (int)section.Offset, (int)section.Size);
            return;
        }

        if (section.Offset + section.Size > pbp.Length)
            throw new PbpFormatException(
                $"PBP section '{section.Name}' runs past the end of the {pbp.Length}-byte stream.");

        pbp.Seek(section.Offset, SeekOrigin.Begin);
        long remaining = section.Size;
        var chunk = new byte[81920];
        while (remaining > 0)
        {
            int want = (int)Math.Min(chunk.Length, remaining);
            int got = pbp.Read(chunk, 0, want);
            if (got <= 0)
                throw new PbpFormatException(
                    $"PBP section '{section.Name}' ended early — {remaining} byte(s) short.");
            output.Write(chunk, 0, got);
            remaining -= got;
        }
    }

    // ---- convenience --------------------------------------------------------

    /// <summary>
    /// Parse the embedded PARAM.SFO via <see cref="ParamSfo.Parse(byte[])"/>, or
    /// return null when the PARAM.SFO section is empty. Reuses the existing SFO
    /// parser — the PBP layer never reimplements SFO parsing.
    /// </summary>
    public static ParamSfo? GetParamSfo(byte[] pbp)
    {
        ArgumentNullException.ThrowIfNull(pbp);
        var file = Parse(pbp);
        var section = file.GetSection("PARAM.SFO");
        if (section.IsEmpty) return null;
        return ParamSfo.Parse(Slice(pbp, section));
    }
}
