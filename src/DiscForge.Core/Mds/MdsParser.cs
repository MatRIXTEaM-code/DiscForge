// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Mds;

/// <summary>Track mode as recorded in an MDS track block.</summary>
public enum MdsTrackMode : byte
{
    None = 0x00,
    Audio = 0xA9,
    Mode1 = 0xAA,
    Mode2 = 0xAB,
    Mode2Form1 = 0xAC,
    Mode2Form2 = 0xAD,
    Mode2Mixed = 0xEC,
}

/// <summary>Sub-channel storage in the MDF.</summary>
public enum MdsSubChannel : byte
{
    None = 0x00,
    /// <summary>96 bytes of interleaved P-W appended to each sector.</summary>
    PwInterleaved = 0x08,
}

/// <summary>Medium the image was taken from.</summary>
public enum MdsMedium : ushort
{
    Cd = 0x00, CdR = 0x01, CdRw = 0x02, Dvd = 0x10, DvdMinusR = 0x12,
}

public sealed record MdsTrack
{
    /// <summary>Track number (1..99).</summary>
    public required int Point { get; init; }
    public required MdsTrackMode Mode { get; init; }
    public required MdsSubChannel SubChannel { get; init; }
    public required byte Adr { get; init; }
    public required byte Control { get; init; }
    public required int SectorSize { get; init; }
    public required uint StartLba { get; init; }
    /// <summary>Byte offset of this track's data within the MDF.</summary>
    public required ulong MdfOffset { get; init; }
    public required uint PregapSectors { get; init; }
    public required uint LengthSectors { get; init; }

    public bool IsAudio => Mode == MdsTrackMode.Audio;
    /// <summary>Bytes this track occupies in the MDF (pregap is stored).</summary>
    public long StoredBytes => (long)(PregapSectors + LengthSectors) * SectorSize;
}

public sealed record MdsSession
{
    public required int Number { get; init; }
    public required int StartLba { get; init; }
    public required int EndLba { get; init; }
    public required int FirstTrack { get; init; }
    public required int LastTrack { get; init; }
    /// <summary>From the 0xA2 lead-in descriptor, decoded from MSF.</summary>
    public required uint LeadOutLba { get; init; }
    public required IReadOnlyList<MdsTrack> Tracks { get; init; }
}

public sealed record MdsImage
{
    public required int VersionMajor { get; init; }
    public required int VersionMinor { get; init; }
    public required MdsMedium Medium { get; init; }
    public required IReadOnlyList<MdsSession> Sessions { get; init; }

    public IEnumerable<MdsTrack> AllTracks => Sessions.SelectMany(s => s.Tracks);
    public int TrackCount => Sessions.Sum(s => s.Tracks.Count);
    public bool HasAudio => AllTracks.Any(t => t.IsAudio);
}

public sealed class MdsFormatException(string message) : Exception(message);

/// <summary>
/// Parses Alcohol 120% MDS (Media Descriptor) files. An Alcohol image is a pair:
/// the .mds describes the layout, the .mdf holds the track data at the byte
/// offsets the descriptor gives.
///
/// Clean-room: the layout comes from public format documentation, not from
/// disassembling Alcohol or inspecting a licensed build. Validated for internal
/// consistency in docs/reference/mds_format.py.
///
/// NOTE: not yet checked against a file produced by Alcohol itself. The same
/// caveat applied to the CDI parser until a real DiscJuggler image proved it —
/// a genuine .mds is the outstanding test here.
/// </summary>
public static class MdsParser
{
    private static ReadOnlySpan<byte> Signature => "MEDIA DESCRIPTOR"u8;

    private const int HeaderSize = 0x58;
    private const int SessionSize = 0x18;
    private const int TrackSize = 0x50;
    private const int ExtraSize = 8;

    /// <summary>CD addressing places LBA 0 at MSF 00:02:00.</summary>
    public const int MsfOffset = 150;

    public static (int Minute, int Second, int Frame) LbaToMsf(long lba)
    {
        long v = lba + MsfOffset;
        return ((int)(v / (60 * 75)), (int)(v / 75 % 60), (int)(v % 75));
    }

    public static int MsfToLba(int minute, int second, int frame)
        => (minute * 60 + second) * 75 + frame - MsfOffset;

    public static MdsImage Parse(ReadOnlySpan<byte> mds)
    {
        if (mds.Length < HeaderSize)
            throw new MdsFormatException(
                $"File is {mds.Length} bytes — too short to be an MDS header ({HeaderSize}).");
        if (!mds[..16].SequenceEqual(Signature))
            throw new MdsFormatException(
                "Not an MDS file: missing the 'MEDIA DESCRIPTOR' signature.");

        int verMajor = mds[0x10];
        int verMinor = mds[0x11];
        var medium = (MdsMedium)BinaryPrimitives.ReadUInt16LittleEndian(mds.Slice(0x12, 2));
        int sessionCount = BinaryPrimitives.ReadUInt16LittleEndian(mds.Slice(0x14, 2));
        int sessionsOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(mds.Slice(0x50, 4));

        if (sessionCount == 0)
            throw new MdsFormatException("MDS declares no sessions.");

        var sessions = new List<MdsSession>(sessionCount);
        for (int i = 0; i < sessionCount; i++)
        {
            int off = sessionsOffset + i * SessionSize;
            Require(mds, off, SessionSize, $"session block {i}");
            sessions.Add(ParseSession(mds, off));
        }

        return new MdsImage
        {
            VersionMajor = verMajor,
            VersionMinor = verMinor,
            Medium = medium,
            Sessions = sessions,
        };
    }

    private static MdsSession ParseSession(ReadOnlySpan<byte> mds, int off)
    {
        int startLba = BinaryPrimitives.ReadInt32LittleEndian(mds.Slice(off + 0x00, 4));
        int endLba = BinaryPrimitives.ReadInt32LittleEndian(mds.Slice(off + 0x04, 4));
        int number = BinaryPrimitives.ReadUInt16LittleEndian(mds.Slice(off + 0x08, 2));
        int allBlocks = mds[off + 0x0A];
        int firstTrack = BinaryPrimitives.ReadUInt16LittleEndian(mds.Slice(off + 0x0C, 2));
        int lastTrack = BinaryPrimitives.ReadUInt16LittleEndian(mds.Slice(off + 0x0E, 2));
        int tracksOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(mds.Slice(off + 0x14, 4));

        var tracks = new List<MdsTrack>();
        uint leadOut = 0;

        for (int j = 0; j < allBlocks; j++)
        {
            int toff = tracksOffset + j * TrackSize;
            Require(mds, toff, TrackSize, $"track block {j}");

            byte point = mds[toff + 0x04];

            // Points 0xA0/0xA1/0xA2 are lead-in descriptors, not data. The
            // lead-out position (0xA2) is stored as MSF in pmin/psec/pframe —
            // not as an LBA.
            if (point == 0xA2)
                leadOut = (uint)Math.Max(0, MsfToLba(mds[toff + 0x09], mds[toff + 0x0A], mds[toff + 0x0B]));
            if (point is < 1 or > 99)
                continue;

            int extraOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(mds.Slice(toff + 0x0C, 4));
            uint pregap = 0, length = 0;
            if (extraOffset > 0 && extraOffset + ExtraSize <= mds.Length)
            {
                pregap = BinaryPrimitives.ReadUInt32LittleEndian(mds.Slice(extraOffset + 0x00, 4));
                length = BinaryPrimitives.ReadUInt32LittleEndian(mds.Slice(extraOffset + 0x04, 4));
            }

            byte adrCtl = mds[toff + 0x02];
            int sectorSize = BinaryPrimitives.ReadUInt16LittleEndian(mds.Slice(toff + 0x10, 2));
            if (sectorSize <= 0)
                throw new MdsFormatException($"Track {point} declares sector size {sectorSize}.");

            tracks.Add(new MdsTrack
            {
                Point = point,
                Mode = (MdsTrackMode)mds[toff + 0x00],
                SubChannel = (MdsSubChannel)mds[toff + 0x01],
                Adr = (byte)((adrCtl >> 4) & 0x0F),
                Control = (byte)(adrCtl & 0x0F),
                SectorSize = sectorSize,
                StartLba = BinaryPrimitives.ReadUInt32LittleEndian(mds.Slice(toff + 0x24, 4)),
                MdfOffset = BinaryPrimitives.ReadUInt64LittleEndian(mds.Slice(toff + 0x28, 8)),
                PregapSectors = pregap,
                LengthSectors = length,
            });
        }

        if (tracks.Count == 0)
            throw new MdsFormatException($"Session {number} contains no data tracks.");

        return new MdsSession
        {
            Number = number,
            StartLba = startLba,
            EndLba = endLba,
            FirstTrack = firstTrack,
            LastTrack = lastTrack,
            LeadOutLba = leadOut,
            Tracks = tracks,
        };
    }

    private static void Require(ReadOnlySpan<byte> mds, int offset, int length, string what)
    {
        if (offset < 0 || offset + length > mds.Length)
            throw new MdsFormatException(
                $"MDS is truncated: {what} at offset {offset} needs {length} bytes, " +
                $"file is {mds.Length}.");
    }
}
