// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Audio;

/// <summary>Raised when a compressed audio source can't be decoded or used as a CD track.</summary>
public sealed class AudioDecodeException(string message) : Exception(message);

/// <summary>
/// Decodes a standalone (container) FLAC file — as opposed to
/// <see cref="DiscForge.Core.Chd.ChdFlac"/>, which decodes the headerless FLAC
/// frames CHD embeds directly in a hunk. Both share the same frame-decoding
/// core: this class parses the "fLaC" container and its STREAMINFO metadata
/// block (sample rate / channels / bits-per-sample, and where the frames
/// start), then hands the frame bytes to <see cref="Chd.ChdFlac"/> — the
/// same decoder already validated byte-for-byte against real chdman output —
/// one frame at a time.
///
/// This exists so an audio CD can be authored straight from a FLAC source
/// (DiscForge's own encoder, or anyone else's): lossless compressed audio is
/// the single most common non-WAV source people actually have, and DiscForge
/// already carries a proven decoder for it.
/// </summary>
public static class FlacDecoder
{
    /// <summary>What a decoded FLAC stream contains, plus its PCM in the
    /// little-endian byte order CD audio (and WAV) sectors use.</summary>
    public sealed record Result(int SampleRate, int Channels, int BitsPerSample, byte[] Pcm);

    /// <summary>
    /// Decode a complete FLAC file's bytes to interleaved little-endian PCM.
    /// <paramref name="name"/> is used only in error messages.
    /// </summary>
    public static Result Decode(byte[] flac, string name)
    {
        ArgumentNullException.ThrowIfNull(flac);

        if (flac.Length < 4 || flac[0] != 'f' || flac[1] != 'L' || flac[2] != 'a' || flac[3] != 'C')
            throw new AudioDecodeException($"'{name}' is not a FLAC file (missing the 'fLaC' marker).");

        int? sampleRate = null, channels = null, bps = null;
        long p = 4;
        while (true)
        {
            if (p + 4 > flac.Length)
                throw new AudioDecodeException($"'{name}': FLAC metadata runs past the end of the file.");

            bool last = (flac[p] & 0x80) != 0;
            int type = flac[p] & 0x7F;
            long len = ((long)flac[p + 1] << 16) | ((long)flac[p + 2] << 8) | flac[p + 3];
            long body = p + 4;
            if (body + len > flac.Length)
                throw new AudioDecodeException($"'{name}': a FLAC metadata block runs past the end of the file.");

            if (type == 0)   // STREAMINFO
            {
                if (len < 34)
                    throw new AudioDecodeException($"'{name}': STREAMINFO block is too short.");
                // Bit layout (FLAC spec), starting at byte 10 of the block: 20 bits
                // sample rate, 3 bits channels-1, 5 bits bits-per-sample-1, 36 bits
                // total samples. The first 10 bytes (two 16-bit block sizes, two
                // 24-bit frame sizes) are byte-aligned and not needed here.
                byte b0 = flac[body + 10], b1 = flac[body + 11], b2 = flac[body + 12], b3 = flac[body + 13];
                sampleRate = (b0 << 12) | (b1 << 4) | (b2 >> 4);
                channels = ((b2 >> 1) & 0x07) + 1;
                bps = (((b2 & 0x01) << 4) | (b3 >> 4)) + 1;
            }

            p = body + len;
            if (last) break;
        }

        if (sampleRate is null || channels is null || bps is null)
            throw new AudioDecodeException($"'{name}': no STREAMINFO block — can't tell what format the audio is.");

        // ChdFlac's subframe decode always folds its output down to 16-bit
        // samples (the CHD convention, since CD audio is always 16-bit); a
        // higher source bit depth would be silently truncated rather than
        // refused, which is exactly the kind of "quiet wrong conversion" this
        // codebase declines to do elsewhere (see WavReader). Catch it here.
        if (bps != 16)
            throw new AudioDecodeException(
                $"'{name}' is {bps}-bit FLAC. DiscForge decodes 16-bit FLAC only " +
                "(Red Book audio is always 16-bit) — convert it first.");

        long frameStart = p;
        var pcmBe = new List<byte>();
        long offset = frameStart;
        while (offset < flac.Length)
        {
            (byte[] Bytes, int Next) frame;
            try
            {
                // wantBytes: 1 makes ChdFlac.Decode's "while (outb.Count < wantBytes)"
                // loop stop after exactly one frame, regardless of that frame's own
                // block size — the trick that lets a whole (length-unknown-up-front)
                // FLAC stream be walked frame by frame.
                frame = Chd.ChdFlac.Decode(flac, (int)offset, wantBytes: 1);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A well-formed encoder ends exactly at the last frame; a few
                // trailing bytes (alignment padding, an appended tag) are common
                // enough in files that didn't come straight from an encoder to be
                // worth tolerating rather than failing the whole decode over.
                if (flac.Length - offset <= 16) break;
                throw new AudioDecodeException($"'{name}' failed to decode as FLAC: {ex.Message}");
            }

            if (frame.Next <= offset)
                throw new AudioDecodeException($"'{name}': FLAC frame decode made no progress — declined.");

            pcmBe.AddRange(frame.Bytes);
            offset = frame.Next;
        }

        var pcm = pcmBe.ToArray();
        // ChdFlac emits big-endian samples (the CHD convention); CD audio / WAV
        // PCM is little-endian.
        for (int i = 0; i + 1 < pcm.Length; i += 2)
            (pcm[i], pcm[i + 1]) = (pcm[i + 1], pcm[i]);

        return new Result(sampleRate.Value, channels.Value, bps.Value, pcm);
    }

    /// <summary>
    /// Decode a FLAC file to a new temporary WAV file (44-byte RIFF header +
    /// raw PCM), for feeding into anything that already speaks WAV. The caller
    /// owns the returned file and must delete it.
    /// </summary>
    public static string DecodeToTempWavFile(string flacPath)
    {
        var bytes = File.ReadAllBytes(flacPath);
        var name = Path.GetFileName(flacPath);
        var result = Decode(bytes, name);

        string tmp = Path.Combine(Path.GetTempPath(), "discforge-audio-" + Guid.NewGuid().ToString("N") + ".wav");
        WriteWavFile(tmp, result.Pcm, result.SampleRate, result.Channels, result.BitsPerSample);
        return tmp;
    }

    /// <summary>Write a minimal, standard-compliant RIFF/WAVE PCM file.</summary>
    internal static void WriteWavFile(string path, byte[] pcm, int sampleRate, int channels, int bitsPerSample)
    {
        int blockAlign = channels * (bitsPerSample / 8);
        int byteRate = sampleRate * blockAlign;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);

        bw.Write("RIFF"u8.ToArray());
        bw.Write(36 + pcm.Length);
        bw.Write("WAVE"u8.ToArray());

        bw.Write("fmt "u8.ToArray());
        bw.Write(16);
        bw.Write((short)1);                 // PCM
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)blockAlign);
        bw.Write((short)bitsPerSample);

        bw.Write("data"u8.ToArray());
        bw.Write(pcm.Length);
        bw.Write(pcm);
    }
}
