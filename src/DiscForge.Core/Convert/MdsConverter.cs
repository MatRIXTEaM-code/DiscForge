// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Cdi;
using DiscForge.Core.Mds;

namespace DiscForge.Core.Convert;

/// <summary>
/// Converts Alcohol 120% MDS/MDF images to CDI.
///
/// The pair splits responsibilities: the .mds says what the disc looked like, the
/// .mdf holds the bytes at the offsets it names. Conversion is therefore mostly
/// a translation of descriptors plus a byte copy — and the copy is streamed, so a
/// DVD-sized MDF never lands in memory.
/// </summary>
public static class MdsConverter
{
    public sealed record ConvertResult(long CdiBytes, int TrackCount, IReadOnlyList<string> Warnings);

    /// <summary>The .mdf that belongs to a .mds, by Alcohol's naming convention.</summary>
    public static string DefaultMdfPath(string mdsPath) =>
        Path.ChangeExtension(mdsPath, ".mdf");

    /// <summary>
    /// Convert an MDS/MDF pair to a CDI image.
    /// </summary>
    /// <param name="mds">Parsed descriptor.</param>
    /// <param name="mdfPath">Path to the .mdf holding the track data.</param>
    public static ConvertResult MdsToCdi(MdsImage mds, string mdfPath, CdiVersion version, Stream output)
    {
        ArgumentNullException.ThrowIfNull(mds);
        ArgumentNullException.ThrowIfNull(output);

        var mdf = new FileInfo(mdfPath);
        if (!mdf.Exists)
            throw new FileNotFoundException(
                $"The descriptor references track data, but '{Path.GetFileName(mdfPath)}' was not " +
                "found next to it. An Alcohol image is a .mds/.mdf pair — both are needed.",
                mdfPath);

        var warnings = new List<string>();
        var sessions = new List<IReadOnlyList<CdiWriter.TrackInput>>();

        foreach (var session in mds.Sessions)
        {
            var inputs = new List<CdiWriter.TrackInput>();
            foreach (var t in session.Tracks)
            {
                if (t.SubChannel != MdsSubChannel.None)
                    warnings.Add(
                        $"Track {t.Point} stores interleaved P-W sub-channel data, which CDI " +
                        "does not carry; it will be dropped.");

                var (mode, sectorSize) = MapTrack(t, warnings);

                long stored = t.StoredBytes;
                long offset = (long)t.MdfOffset;
                if (offset < 0 || offset + stored > mdf.Length)
                    throw new InvalidDataException(
                        $"Track {t.Point} claims {stored:N0} bytes at offset {offset:N0}, but " +
                        $"'{mdf.Name}' is only {mdf.Length:N0} bytes. The .mds and .mdf may not " +
                        "be a matching pair.");

                string path = mdf.FullName;
                inputs.Add(new CdiWriter.TrackInput
                {
                    Mode = mode,
                    SectorSize = sectorSize,
                    PregapSectors = t.PregapSectors,
                    LengthSectors = t.LengthSectors,
                    StartLba = t.StartLba,
                    Filename = $"TRACK{t.Point:D2}.BIN",
                    // Streamed straight out of the MDF at its recorded offset.
                    DataWriter = os => CopyRange(path, offset, stored, os),
                });
            }
            if (inputs.Count > 0) sessions.Add(inputs);
        }

        if (sessions.Count == 0)
            throw new InvalidDataException("The MDS contains no convertible tracks.");

        long start = output.CanSeek ? output.Position : 0;
        CdiWriter.Write(output, version, sessions);
        long written = output.CanSeek ? output.Position - start : 0;

        return new ConvertResult(written, sessions.Sum(s => s.Count), warnings);
    }

    /// <summary>Map an MDS track mode + sector size onto CDI's model.</summary>
    private static (CdiTrackMode Mode, CdiSectorSize Size) MapTrack(MdsTrack t, List<string> warnings)
    {
        var mode = t.Mode switch
        {
            MdsTrackMode.Audio => CdiTrackMode.Audio,
            MdsTrackMode.Mode1 => CdiTrackMode.Mode1,
            MdsTrackMode.Mode2 or MdsTrackMode.Mode2Form1
                or MdsTrackMode.Mode2Form2 or MdsTrackMode.Mode2Mixed => CdiTrackMode.Mode2,
            _ => throw new InvalidDataException(
                $"Track {t.Point} has unsupported mode 0x{(byte)t.Mode:X2}."),
        };

        // CDI stores 2048 / 2336 / 2352. Anything else (e.g. 2448 = sector plus
        // 96 bytes of sub-channel) cannot be represented faithfully.
        var size = t.SectorSize switch
        {
            2048 => CdiSectorSize.S2048,
            2336 => CdiSectorSize.S2336,
            2352 => CdiSectorSize.S2352,
            2448 => throw new InvalidDataException(
                $"Track {t.Point} stores 2448-byte sectors (2352 + 96 bytes of sub-channel). " +
                "CDI has no sub-channel form, so this image cannot be converted without " +
                "discarding data."),
            _ => throw new InvalidDataException(
                $"Track {t.Point} has sector size {t.SectorSize}, which CDI does not support " +
                "(expected 2048, 2336 or 2352)."),
        };

        return (mode, size);
    }

    private static void CopyRange(string path, long offset, long count, Stream output)
    {
        using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                       bufferSize: 1 << 16, FileOptions.SequentialScan);
        src.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[1 << 16];
        long remaining = count;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int n = src.Read(buffer, 0, want);
            if (n <= 0)
                throw new EndOfStreamException(
                    $"'{Path.GetFileName(path)}' ended {remaining:N0} bytes early.");
            output.Write(buffer, 0, n);
            remaining -= n;
        }
    }
}
