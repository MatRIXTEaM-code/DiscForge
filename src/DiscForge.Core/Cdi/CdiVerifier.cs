// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Util;

namespace DiscForge.Core.Cdi;

public enum VerifySeverity { Info, Warning, Error }

public sealed record VerifyIssue(VerifySeverity Severity, string Message);

public sealed record TrackChecksum
{
    public required int TrackNumber { get; init; }
    public required long StoredBytes { get; init; }
    public required uint StoredCrc32 { get; init; }
    /// <summary>CRC of cooked user data (null if not requested).</summary>
    public long? UserBytes { get; init; }
    public uint? UserCrc32 { get; init; }
}

public sealed record VerifyReport
{
    public required bool Passed { get; init; }
    public required IReadOnlyList<VerifyIssue> Issues { get; init; }
    public required IReadOnlyList<TrackChecksum> Checksums { get; init; }

    public bool HasErrors => Issues.Any(i => i.Severity == VerifySeverity.Error);
    public bool HasWarnings => Issues.Any(i => i.Severity == VerifySeverity.Warning);
}

/// <summary>
/// Verifies a CDI image: structural self-consistency plus per-track checksums.
/// Structural checks are pure reasoning over the parsed model + file length;
/// checksums stream the actual track bytes. Errors mean the image is malformed
/// or internally contradictory; warnings mean unusual-but-not-fatal.
/// </summary>
public static class CdiVerifier
{
    public static VerifyReport Verify(Stream cdi, CdiImage image, bool computeUserChecksums = false)
    {
        ArgumentNullException.ThrowIfNull(cdi);
        ArgumentNullException.ThrowIfNull(image);

        var issues = new List<VerifyIssue>();
        var checksums = new List<TrackChecksum>();

        void Err(string m) => issues.Add(new VerifyIssue(VerifySeverity.Error, m));
        void Warn(string m) => issues.Add(new VerifyIssue(VerifySeverity.Warning, m));

        // --- Structural: descriptor placement ---
        if (image.DescriptorOffset < 0 || image.DescriptorOffset > image.FileLength - CdiParser.TrailerLength)
            Err($"Descriptor offset {image.DescriptorOffset} outside file (length {image.FileLength}).");

        if (image.TrackCount == 0)
            Warn("Image contains no tracks.");

        // --- Structural: per-track + offset accumulation ---
        long expectedOffset = 0;
        int? lastSession = null;
        long lastLbaInSession = -1;

        foreach (var t in image.AllTracks)
        {
            if (t.TotalSectors < t.PregapSectors + t.LengthSectors)
                Err($"Track {t.Number}: total {t.TotalSectors} < pregap {t.PregapSectors} + length {t.LengthSectors}.");

            if (t.LengthSectors == 0)
                Warn($"Track {t.Number}: zero-length content.");

            if (t.FileOffset != expectedOffset)
                Err($"Track {t.Number}: file offset {t.FileOffset} != expected {expectedOffset} " +
                    "(track data not contiguous from start).");

            long end = t.FileOffset + t.StoredByteLength;
            if (end > image.DescriptorOffset)
                Err($"Track {t.Number}: data ends at {end}, past descriptor start {image.DescriptorOffset}.");

            // LBA monotonicity within a session (informational — pressed discs vary).
            if (lastSession != t.SessionIndex) { lastSession = t.SessionIndex; lastLbaInSession = -1; }
            if (t.StartLba <= lastLbaInSession)
                Warn($"Track {t.Number}: start LBA {t.StartLba} not increasing within session {t.SessionIndex}.");
            lastLbaInSession = t.StartLba;

            expectedOffset = end;
        }

        // --- Checksums (stream actual bytes) ---
        foreach (var t in image.AllTracks)
        {
            uint storedCrc;
            try { storedCrc = StoredCrc(cdi, t); }
            catch (Exception ex)
            {
                Err($"Track {t.Number}: cannot read stored data ({ex.Message}).");
                continue;
            }

            uint? userCrc = null;
            long? userBytes = null;
            if (computeUserChecksums)
            {
                try
                {
                    // Stream the cooked user data through a CRC sink rather than
                    // buffering it: a DVD track is far past the 2 GB MemoryStream
                    // ceiling, and the old code failed on exactly that
                    // ("Stream was too long") for any image over ~2 GB.
                    using var sink = new Crc32Sink();
                    CdiExtractor.ExtractUserData(cdi, t, sink);
                    userCrc = sink.Value;
                    userBytes = sink.Length;
                }
                catch (Exception ex)
                {
                    Warn($"Track {t.Number}: user-data checksum skipped ({ex.Message}).");
                }
            }

            checksums.Add(new TrackChecksum
            {
                TrackNumber = t.Number,
                StoredBytes = t.StoredByteLength,
                StoredCrc32 = storedCrc,
                UserBytes = userBytes,
                UserCrc32 = userCrc,
            });
        }

        bool passed = !issues.Any(i => i.Severity == VerifySeverity.Error);
        return new VerifyReport { Passed = passed, Issues = issues, Checksums = checksums };
    }

    private static uint StoredCrc(Stream cdi, CdiTrack track)
    {
        cdi.Seek(track.FileOffset, SeekOrigin.Begin);
        var crc = new Crc32();
        long remaining = track.StoredByteLength;
        var buf = new byte[64 * 1024];
        while (remaining > 0)
        {
            int n = (int)Math.Min(remaining, buf.Length);
            cdi.ReadExactly(buf, 0, n);
            crc.Update(buf.AsSpan(0, n));
            remaining -= n;
        }
        return crc.Value;
    }

    /// <summary>
    /// A write-only stream that CRC-32s whatever is written and keeps none of it.
    /// Lets a track of any size be checksummed in constant memory — a DVD's user
    /// data is far past what a MemoryStream can hold.
    /// </summary>
    private sealed class Crc32Sink : Stream
    {
        private readonly Crc32 _crc = new();
        private long _length;

        public uint Value => _crc.Value;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _crc.Update(buffer.AsSpan(offset, count));
            _length += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _crc.Update(buffer);
            _length += buffer.Length;
        }

        public override void WriteByte(byte value)
        {
            Span<byte> one = stackalloc byte[1];
            one[0] = value;
            _crc.Update(one);
            _length++;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
