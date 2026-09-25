// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;

namespace DiscForge.Core.PlayStation;

public sealed class VabFormatException(string message) : Exception(message);

/// <summary>One tone (velocity/key split) inside a VAB program.</summary>
public sealed class VabTone
{
    public required int Volume { get; init; }
    public required int Pan { get; init; }
    public required int CenterNote { get; init; }
    public required int NoteLow { get; init; }
    public required int NoteHigh { get; init; }
    /// <summary>1-based index of the VAG waveform this tone plays (0 = none).</summary>
    public required int Vag { get; init; }
}

/// <summary>One program (instrument) in a VAB bank.</summary>
public sealed class VabProgram
{
    public required int Index { get; init; }
    public required int ToneCount { get; init; }
    public required int Volume { get; init; }
    public required int Pan { get; init; }
    public required IReadOnlyList<VabTone> Tones { get; init; }
}

/// <summary>
/// A parsed PlayStation VAB (VAG bank) — the SPU instrument bank half of a
/// VAB+SEQ pair. Structure only: programs, tones and the waveform pointer table.
/// No audio is synthesised.
/// </summary>
public sealed class VabFile
{
    public required int Version { get; init; }
    public required int VabId { get; init; }
    public required int ProgramCount { get; init; }
    public required int ToneCount { get; init; }
    public required int VagCount { get; init; }
    public required int MasterVolume { get; init; }
    public required int MasterPan { get; init; }
    public required IReadOnlyList<VabProgram> Programs { get; init; }
    /// <summary>Waveform sizes in bytes (pointer table entry × 8), index 1..VagCount.</summary>
    public required IReadOnlyList<int> VagSizes { get; init; }
    /// <summary>Running byte offset of each waveform within the VAG data area.</summary>
    public required IReadOnlyList<int> VagOffsets { get; init; }
}

/// <summary>
/// Reads the VAB header, program headers and tone attributes.
///
/// Clean-room, from the public VAB description. LITTLE-ENDIAN throughout.
///   Header (0x20 bytes):
///     0x00 4  form — bytes "pBAV" (0x70 0x42 0x41 0x56); "VABp" also accepted
///     0x04 4  version
///     0x08 4  vab id
///     0x0C 4  total size
///     0x10 2  reserved
///     0x12 2  number of programs
///     0x14 2  number of tones (total)
///     0x16 2  number of VAGs (waveforms)
///     0x18 1  master volume
///     0x19 1  master pan
///     ...
///   Then 128 program headers (16 bytes each):
///     0 tones  1 vol  2 prior  3 mode  4 pan ...
///   Then, for each present program, 16 tone attributes (32 bytes each):
///     0 prior 1 mode 2 vol 3 pan 4 center 5 shift 6 min 7 max ... 22 vag(u16) ...
///   Then 256 VAG pointers (u16, size/8); waveform offset = running sum × 8.
/// </summary>
public static class Vab
{
    private const int HeaderSize = 0x20;
    private const int ProgramHeaderCount = 128;
    private const int ProgramHeaderSize = 16;
    private const int TonesPerProgram = 16;
    private const int ToneAttrSize = 32;
    private const int VagPointerCount = 256;

    public static bool IsVab(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return false;
        // "pBAV" (the byte order given by the format notes) or the ASCII "VABp".
        bool pBAV = data[0] == 0x70 && data[1] == 0x42 && data[2] == 0x41 && data[3] == 0x56;
        bool vABp = data[0] == 0x56 && data[1] == 0x41 && data[2] == 0x42 && data[3] == 0x70;
        return pBAV || vABp;
    }

    public static VabFile Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < HeaderSize || !IsVab(data))
            throw new VabFormatException("Missing the VAB signature — not a VAB bank.");

        var span = data.AsSpan();
        int version = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0x04));
        int vabId = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0x08));
        int programCount = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(0x12));
        int toneCount = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(0x14));
        int vagCount = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(0x16));
        int masterVol = data[0x18];
        int masterPan = data[0x19];

        if (programCount > ProgramHeaderCount)
            throw new VabFormatException($"Program count {programCount} exceeds 128.");

        // Program header table: 128 fixed slots. A slot with zero tones is unused.
        int progTableEnd = HeaderSize + ProgramHeaderCount * ProgramHeaderSize;
        if (progTableEnd > data.Length)
            throw new VabFormatException("Truncated before the end of the program header table.");

        // Read the raw program-header fields for all 128 slots.
        var rawTones = new int[ProgramHeaderCount];
        var rawVol = new int[ProgramHeaderCount];
        var rawPan = new int[ProgramHeaderCount];
        int present = 0;
        for (int i = 0; i < ProgramHeaderCount; i++)
        {
            int off = HeaderSize + i * ProgramHeaderSize;
            rawTones[i] = data[off + 0];
            rawVol[i] = data[off + 1];
            rawPan[i] = data[off + 4];
            if (rawTones[i] > 0) present++;
        }

        // Tone attribute tables follow, one 16×32 block per *present* program, in order.
        int programsWithTones = present > 0 ? present : programCount;
        int toneTablesEnd = progTableEnd + programsWithTones * TonesPerProgram * ToneAttrSize;

        var programs = new List<VabProgram>();
        int toneBlock = 0;
        for (int i = 0; i < ProgramHeaderCount; i++)
        {
            if (rawTones[i] <= 0) continue;
            int blockOff = progTableEnd + toneBlock * TonesPerProgram * ToneAttrSize;
            toneBlock++;

            var tones = new List<VabTone>();
            for (int t = 0; t < rawTones[i] && t < TonesPerProgram; t++)
            {
                int to = blockOff + t * ToneAttrSize;
                if (to + ToneAttrSize > data.Length) break;
                tones.Add(new VabTone
                {
                    Volume = data[to + 2],
                    Pan = data[to + 3],
                    CenterNote = data[to + 4],
                    NoteLow = data[to + 6],
                    NoteHigh = data[to + 7],
                    Vag = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(to + 22)),
                });
            }

            programs.Add(new VabProgram
            {
                Index = i,
                ToneCount = rawTones[i],
                Volume = rawVol[i],
                Pan = rawPan[i],
                Tones = tones,
            });
        }

        // VAG pointer table: 256 u16 (size/8). Entry 0 is unused; sizes accumulate.
        var vagSizes = new List<int>();
        var vagOffsets = new List<int>();
        if (toneTablesEnd + VagPointerCount * 2 <= data.Length)
        {
            int running = 0;
            for (int i = 0; i < VagPointerCount; i++)
            {
                int ptr = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(toneTablesEnd + i * 2));
                int size = ptr * 8;
                vagOffsets.Add(running);
                vagSizes.Add(size);
                running += size;
            }
        }

        return new VabFile
        {
            Version = version,
            VabId = vabId,
            ProgramCount = programCount,
            ToneCount = toneCount,
            VagCount = vagCount,
            MasterVolume = masterVol,
            MasterPan = masterPan,
            Programs = programs,
            VagSizes = vagSizes,
            VagOffsets = vagOffsets,
        };
    }
}
