// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Cdi;

/// <summary>
/// Extracts track content from a CDI image. Handles "sector cooking" — pulling
/// clean user data out of stored sectors — using a model verified end-to-end:
/// the Mode2/Form1 2336-byte path was confirmed by reconstructing a real ISO
/// byte-for-byte from a genuine cdi4dc image (docs/reference/validate_cdi.py
/// lineage). See docs/CDI_FORMAT.md §5.
/// </summary>
public static class CdiExtractor
{
    /// <summary>User-data window (offset, length) within one stored sector,
    /// as a function of stored sector size and track mode.</summary>
    public static (int offset, int length) UserDataWindow(CdiSectorSize sectorSize, CdiTrackMode mode)
    {
        return (sectorSize, mode) switch
        {
            // Already-cooked user data.
            (CdiSectorSize.S2048, _) => (0, 2048),

            // Mode 2 Form 1 stored as 2336: 8-byte subheader, then 2048 user.
            // (Confirmed: real cdi4dc data track, user data at +8.)
            (CdiSectorSize.S2336, _) => (8, 2048),

            // Full raw 2352:
            //  Mode 1        : 12 sync + 4 header + 2048 user + EDC/ECC  -> user @16
            //  Mode 2 Form 1 : 12 sync + 4 header + 8 subhdr + 2048 user -> user @24
            //  Audio         : whole sector is PCM, no user-data cooking.
            (CdiSectorSize.S2352, CdiTrackMode.Mode1) => (16, 2048),
            (CdiSectorSize.S2352, CdiTrackMode.Mode2) => (24, 2048),
            (CdiSectorSize.S2352, CdiTrackMode.Audio) => (0, 2352),

            _ => throw new NotSupportedException(
                $"No cooking rule for {sectorSize}/{mode}."),
        };
    }

    /// <summary>
    /// Extracts a track's cooked user data (the ISO/BIN payload for data tracks,
    /// or raw PCM for audio) to <paramref name="output"/>. Pregap sectors are
    /// skipped; only the LengthSectors content sectors are emitted.
    /// </summary>
    public static long ExtractUserData(Stream cdi, CdiTrack track, Stream output)
    {
        ArgumentNullException.ThrowIfNull(cdi);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(output);

        int sectorBytes = (int)track.SectorSize;
        var (userOff, userLen) = UserDataWindow(track.SectorSize, track.Mode);

        // Content starts after the pregap sectors within the track's stored region.
        long contentStart = track.FileOffset + (long)track.PregapSectors * sectorBytes;

        var sector = new byte[sectorBytes];
        long written = 0;
        for (uint i = 0; i < track.LengthSectors; i++)
        {
            cdi.Seek(contentStart + (long)i * sectorBytes, SeekOrigin.Begin);
            cdi.ReadExactly(sector, 0, sectorBytes);
            output.Write(sector, userOff, userLen);
            written += userLen;
        }
        return written;
    }

    /// <summary>
    /// Extracts a track's raw stored sectors verbatim (no cooking, pregap
    /// included) — the faithful representation for round-tripping or when the
    /// caller wants BIN with full sectors.
    /// </summary>
    public static long ExtractRaw(Stream cdi, CdiTrack track, Stream output)
    {
        ArgumentNullException.ThrowIfNull(cdi);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(output);

        cdi.Seek(track.FileOffset, SeekOrigin.Begin);
        long remaining = track.StoredByteLength;
        var buf = new byte[64 * 1024];
        while (remaining > 0)
        {
            int n = (int)Math.Min(remaining, buf.Length);
            cdi.ReadExactly(buf, 0, n);
            output.Write(buf, 0, n);
            remaining -= n;
        }
        return track.StoredByteLength;
    }

    /// <summary>
    /// Extracts an audio track to a canonical 16-bit/44.1kHz/stereo WAV.
    /// CD audio is exactly that PCM format; each 2352-byte sector = 588 frames.
    /// </summary>
    public static void ExtractAudioToWav(Stream cdi, CdiTrack track, Stream output)
    {
        if (track.Mode != CdiTrackMode.Audio)
            throw new ArgumentException("Track is not audio.", nameof(track));

        long pcmBytes = (long)track.LengthSectors * 2352;
        WriteWavHeader(output, pcmBytes);

        // Audio "user window" is the whole sector; reuse ExtractUserData.
        ExtractUserData(cdi, track, output);
    }

    private static void WriteWavHeader(Stream s, long pcmBytes)
    {
        const int sampleRate = 44100, channels = 2, bits = 16;
        int byteRate = sampleRate * channels * (bits / 8);
        short blockAlign = channels * (bits / 8);

        using var w = new BinaryWriter(s, System.Text.Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF"u8.ToArray());
        w.Write((uint)(36 + pcmBytes));
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16u);                          // fmt chunk size
        w.Write((short)1);                     // PCM
        w.Write((short)channels);
        w.Write((uint)sampleRate);
        w.Write((uint)byteRate);
        w.Write(blockAlign);
        w.Write((short)bits);
        w.Write("data"u8.ToArray());
        w.Write((uint)pcmBytes);
    }
}
