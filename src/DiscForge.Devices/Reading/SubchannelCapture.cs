// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Runtime.Versioning;
using DiscForge.Core.Raw;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices.Reading;

/// <summary>What a sub-channel capture produced.</summary>
public sealed record SubchannelCaptureResult
{
    public required string Path { get; init; }
    public required uint SectorsWritten { get; init; }
    public required uint SectorsRefused { get; init; }
    /// <summary>Analysis of what was captured — whether the Q frames validate,
    /// and whether the failures look deliberate.</summary>
    public required RawSubchannel.Analysis Analysis { get; init; }

    public long Bytes => (long)SectorsWritten * RawSubchannel.FrameSize;
    public bool Complete => SectorsRefused == 0;
}

/// <summary>
/// Captures a disc's sub-channel to a .sub sidecar beside its image.
///
/// Why this matters for preservation. Some discs carry meaning in their
/// sub-channel that the main data does not: LibCrypt and its relatives corrupt
/// specific Q frames deliberately, and the software reads those positions back
/// to confirm it is running from an original. An image without the sub-channel
/// is a perfect copy of the data that will not run — every byte correct, and
/// useless, because the thing being checked was never captured.
///
/// CD+G discs are the same case with a happier purpose: the graphics live in the
/// R–W channels and are simply absent from an ordinary image.
///
/// Deliberately a second pass rather than woven into the main read. Asking for
/// sub-channel alongside the data would change the bytes-per-sector arithmetic
/// throughout the reader, and that path is more valuable working than it is
/// efficient. Reading twice costs time; getting the image wrong costs the image.
///
/// The format written is the CloneCD convention — 96 bytes per sector, raw
/// interleaved P–W, one frame per sector of the track, named to match the image.
/// That is what other tools expect to find, and interoperating with them is most
/// of the point of writing a sidecar at all.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SubchannelCapture
{
    /// <summary>
    /// Read a disc's sub-channel and write it beside <paramref name="imagePath"/>
    /// as a .sub file.
    /// </summary>
    /// <param name="imagePath">The image just written; the sidecar takes its name.</param>
    /// <param name="startLba">First sector — normally 0.</param>
    /// <param name="sectorCount">How many sectors the image covers.</param>
    /// <returns>
    /// The result, or null when the drive will not return raw sub-channel at
    /// all. That is a refusal rather than a failure: plenty of drives cannot do
    /// it, the image is unaffected, and the caller should say so plainly rather
    /// than treating it as an error.
    /// </returns>
    public static SubchannelCaptureResult? Capture(char driveLetter, string imagePath,
                                                   uint startLba, uint sectorCount,
                                                   IProgress<double>? progress = null,
                                                   CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(imagePath);
        if (sectorCount == 0) return null;

        using var dev = new SptiDevice(driveLetter);

        if (!SubchannelReader.SupportsRawSubchannel(dev, startLba))
            return null;

        var read = SubchannelReader.Read(dev, startLba, sectorCount, progress, cancel);

        string subPath = Path.ChangeExtension(imagePath, ".sub");

        // Written to a .partial and renamed on success, like the image itself.
        // A truncated sidecar is worse than none: it looks like a valid capture
        // and silently describes the wrong sectors from wherever it stops.
        string partial = subPath + ".partial";
        try
        {
            File.WriteAllBytes(partial, read.Subcode);

            if (File.Exists(subPath)) File.Delete(subPath);
            File.Move(partial, subPath);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }

        using var ms = new MemoryStream(read.Subcode);
        var analysis = RawSubchannel.Analyse(ms);

        return new SubchannelCaptureResult
        {
            Path = subPath,
            SectorsWritten = sectorCount,
            SectorsRefused = read.SectorsRefused,
            Analysis = analysis,
        };
    }

    /// <summary>
    /// A plain-language account of what was captured and whether it matters.
    ///
    /// Most discs have nothing of interest in their sub-channel, and saying so
    /// is useful: a user who captured it "just in case" deserves to know the
    /// sidecar is unremarkable rather than wondering what they now have.
    /// </summary>
    public static string Describe(SubchannelCaptureResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var a = result.Analysis;

        if (a.LooksLikeLibCrypt)
            return $"Sub-channel captured to {Path.GetFileName(result.Path)} — and it matters: " +
                   $"{a.QInvalid} Q frame(s) are deliberately corrupt, the signature of " +
                   "LibCrypt-style protection. An image without this sidecar would be a " +
                   "perfect copy of the data that would not run, because the software checks " +
                   "those exact frames. Keep the two files together.";

        if (a.QInvalid == 0)
            return $"Sub-channel captured to {Path.GetFileName(result.Path)}. Every Q frame " +
                   "validates, so there is no protection of this kind on the disc and nothing " +
                   "unusual to preserve — but the sidecar costs little and completes the copy.";

        double rate = a.Frames == 0 ? 0 : (double)a.QInvalid / a.Frames;
        if (rate > 0.02)
            return $"Sub-channel captured to {Path.GetFileName(result.Path)}, but {a.QInvalid:N0} " +
                   $"Q frame(s) ({rate:P1}) failed their CRC. At that rate it reads as damage " +
                   "rather than protection — the sidecar records what the drive returned, which " +
                   "on a damaged disc is not necessarily what was written.";

        return $"Sub-channel captured to {Path.GetFileName(result.Path)}. {a.QInvalid} Q frame(s) " +
               "failed their CRC — too few for damage, too many to dismiss. Worth analysing: " +
               "deliberate corruption is identical on every read, while marginal media varies.";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}