// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.PlayStation;

public sealed class PsExeFormatException(string message) : Exception(message);

/// <summary>
/// Reads the header of a PlayStation executable (a "PS-EXE") — the format the
/// boot loader loads and the format tools like ApplyEXE inspect and patch. It is
/// a plain, unencrypted 2 KB header ("PS-X EXE") followed by the code/data image;
/// this reads the load layout (entry point, load address, sizes, stack) and the
/// region marker, which is what a person needs to identify or re-pad an EXE. It
/// decrypts nothing and defeats nothing — a PS-EXE carries no protection.
///
/// Clean-room, from the public PS-EXE header description:
///
///   0x00  8  "PS-X EXE"
///   0x10  4  initial PC          (entry point)
///   0x14  4  initial GP
///   0x18  4  load address (t_addr) — where the TEXT image is copied in RAM
///   0x1C  4  TEXT size (t_size)   — a multiple of 0x800
///   0x28  4  BSS address
///   0x2C  4  BSS size
///   0x30  4  initial SP base
///   0x34  4  initial SP offset
///   0x4C  …  ASCII region/licence marker
///   payload begins at file offset 0x800, length t_size.
/// </summary>
public static class PsExe
{
    public const int HeaderSize = 0x800;
    private static readonly byte[] Magic = "PS-X EXE"u8.ToArray();

    public sealed record PsExeHeader
    {
        public required uint EntryPoint { get; init; }   // initial PC
        public required uint Gp { get; init; }
        public required uint LoadAddress { get; init; }  // t_addr
        public required uint TextSize { get; init; }     // t_size
        public required uint BssAddress { get; init; }
        public required uint BssSize { get; init; }
        public required uint StackBase { get; init; }
        public required uint StackOffset { get; init; }
        /// <summary>The ASCII region/licence string at 0x4C (trimmed), e.g. the
        /// "Sony Computer Entertainment Inc. for … area" marker.</summary>
        public required string RegionMarker { get; init; }

        public string Summary =>
            $"PS-EXE: entry 0x{EntryPoint:X8}, loads {TextSize:N0} bytes at 0x{LoadAddress:X8}" +
            (RegionMarker.Length > 0 ? $"  [{RegionMarker}]" : "");
    }

    public static bool IsPsExe(ReadOnlySpan<byte> data) =>
        data.Length >= Magic.Length && data[..Magic.Length].SequenceEqual(Magic);

    public static PsExeHeader ReadHeader(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < HeaderSize)
            throw new PsExeFormatException(
                $"File is only {data.Length:N0} bytes — a PS-EXE header is {HeaderSize} bytes.");
        if (!IsPsExe(data))
            throw new PsExeFormatException("Missing the \"PS-X EXE\" signature — not a PlayStation executable.");

        uint U(int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at, 4));

        // The region marker runs from 0x4C to the end of the header, NUL/space padded.
        int end = 0x4C;
        while (end < HeaderSize && data[end] != 0) end++;
        string marker = Encoding.ASCII.GetString(data, 0x4C, end - 0x4C).Trim();

        return new PsExeHeader
        {
            EntryPoint = U(0x10),
            Gp = U(0x14),
            LoadAddress = U(0x18),
            TextSize = U(0x1C),
            BssAddress = U(0x28),
            BssSize = U(0x2C),
            StackBase = U(0x30),
            StackOffset = U(0x34),
            RegionMarker = marker,
        };
    }
}
