// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Diagnostics;
using System.Drawing;
using System.Globalization;

namespace DiscForge.App.Views;

/// <summary>
/// Grabs a single still frame from a video with FFmpeg — used to show a live
/// preview of the frame being processed during a conversion. It seeks to a time
/// and decodes one frame to a small JPEG, then loads it as an independent image
/// (copied off the temp file so the file can be deleted at once). Best-effort: any
/// failure returns null and the caller simply shows no new frame.
/// </summary>
internal static class VideoPreview
{
    public static Image? Grab(string ffmpegPath, string inputPath, double seconds, int width = 240)
    {
        string tmp = Path.Combine(Path.GetTempPath(),
            "discforge-preview-" + Guid.NewGuid().ToString("N")[..8] + ".jpg");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            // Input-seek (before -i) is fast; one frame, scaled down, moderate quality.
            foreach (var a in new[]
            {
                "-y", "-loglevel", "error",
                "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", inputPath,
                "-frames:v", "1",
                "-vf", $"scale={width}:-1",
                "-q:v", "5",
                tmp,
            })
                psi.ArgumentList.Add(a);

            using (var p = Process.Start(psi))
            {
                if (p is null) return null;
                p.StandardError.ReadToEnd();
                if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return null; }
            }

            if (!File.Exists(tmp) || new FileInfo(tmp).Length == 0) return null;
            using var loaded = Image.FromFile(tmp);
            return new Bitmap(loaded);   // an independent copy, not tied to the file
        }
        catch
        {
            return null;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
        }
    }
}
