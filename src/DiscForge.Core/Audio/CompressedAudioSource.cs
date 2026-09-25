// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Transcode;

namespace DiscForge.Core.Audio;

/// <summary>
/// Turns whatever audio file a track source points at into a WAV path
/// <see cref="AudioCdCreator"/> can read directly.
///
/// Three tiers, cheapest and most trustworthy first:
///  - <c>.wav</c> — used as-is, no extra step.
///  - <c>.flac</c> — decoded in-process by <see cref="FlacDecoder"/>, DiscForge's
///    own clean-room decoder. No external dependency, and losslessly exact.
///  - anything else FFmpeg recognises as audio (MP3, AAC/M4A, Ogg Vorbis, WMA,
///    APE, Musepack, WavPack…) — DiscForge does not carry a decoder for any of
///    these (most are patent- or complexity-heavy to reimplement, and a
///    half-right lossy decoder is worse than none), so it shells out to an
///    installed FFmpeg, exactly as <see cref="Transcode.FfmpegRunner"/> already
///    does for video. If FFmpeg isn't found, this fails with a clear message
///    rather than a cryptic one — the same "don't bundle it, but don't hide
///    why it's missing" stance <see cref="FfmpegRunner"/> documents.
///
/// Either compressed path is decoded to a temporary 44.1 kHz/16-bit/stereo WAV;
/// the caller is responsible for deleting it once the burn/build finishes with
/// the source (<see cref="AudioCdCreator"/> does this itself).
/// </summary>
public static class CompressedAudioSource
{
    /// <summary>Extensions FFmpeg is asked to handle. Not exhaustive — FFmpeg
    /// itself decides whether it can actually read the file — but limiting the
    /// attempt to plausible audio containers means a genuinely wrong file (a
    /// video, a text file with the wrong extension) fails with FFmpeg's own
    /// reason rather than DiscForge silently trying to burn garbage.</summary>
    private static readonly HashSet<string> FfmpegExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".aac", ".m4a", ".mp4", ".ogg", ".oga", ".wma", ".ape", ".mpc", ".wv",
    };

    public sealed record Prepared(string WavPath, bool IsTemporary);

    /// <summary>
    /// Resolve <paramref name="sourcePath"/> to a WAV file, decoding/transcoding
    /// first if it isn't one already. Throws <see cref="AudioDecodeException"/>
    /// (compressed formats needing FFmpeg) or lets <see cref="FlacDecoder"/>'s
    /// own exception through (FLAC) when the source can't be used.
    /// </summary>
    public static Prepared Prepare(string sourcePath, string? ffmpegPath = null)
    {
        string ext = Path.GetExtension(sourcePath);

        if (ext.Equals(".wav", StringComparison.OrdinalIgnoreCase))
            return new Prepared(sourcePath, IsTemporary: false);

        if (ext.Equals(".flac", StringComparison.OrdinalIgnoreCase))
            return new Prepared(FlacDecoder.DecodeToTempWavFile(sourcePath), IsTemporary: true);

        if (FfmpegExtensions.Contains(ext))
            return new Prepared(DecodeWithFfmpeg(sourcePath, ffmpegPath), IsTemporary: true);

        throw new AudioDecodeException(
            $"'{Path.GetFileName(sourcePath)}' is a '{ext}' file. DiscForge authors audio CDs from " +
            "WAV and FLAC directly, and from MP3/AAC/M4A/OGG/WMA/APE/MPC/WV via an installed FFmpeg. " +
            "Convert it to one of those first, or install FFmpeg (https://ffmpeg.org) and put it on PATH.");
    }

    private static string DecodeWithFfmpeg(string sourcePath, string? ffmpegPath)
    {
        string name = Path.GetFileName(sourcePath);
        string? ffmpeg = FfmpegRunner.Locate(ffmpegPath);
        if (ffmpeg is null)
            throw new AudioDecodeException(
                $"'{name}' needs FFmpeg to decode (DiscForge has no built-in {Path.GetExtension(sourcePath).TrimStart('.').ToUpperInvariant()} " +
                "decoder), but no FFmpeg was found on PATH. Install it from https://ffmpeg.org, or " +
                "convert the file to WAV or FLAC yourself first.");

        string tmp = Path.Combine(Path.GetTempPath(), "discforge-audio-" + Guid.NewGuid().ToString("N") + ".wav");
        var log = new List<string>();
        int code;
        try
        {
            code = FfmpegRunner.RunProcess(ffmpeg,
                new[] { "-y", "-i", sourcePath, "-vn", "-ar", "44100", "-ac", "2", "-sample_fmt", "s16", "-f", "wav", tmp },
                log.Add);
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            throw new AudioDecodeException($"Running FFmpeg on '{name}' failed to start: {ex.Message}");
        }

        if (code != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length == 0)
        {
            TryDelete(tmp);
            string tail = string.Join(" / ", log.TakeLast(3));
            throw new AudioDecodeException(
                $"FFmpeg could not decode '{name}' (exit code {code}){(tail.Length > 0 ? ": " + tail : ".")}");
        }

        return tmp;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
