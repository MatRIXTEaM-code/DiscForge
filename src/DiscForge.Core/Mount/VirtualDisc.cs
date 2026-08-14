// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Files;

namespace DiscForge.Core.Mount;

/// <summary>
/// The emulation model behind "mount this image as a drive" — the part that is
/// pure and testable, kept separate from the OS device plumbing that actually
/// presents a drive letter.
///
/// Two layers, deliberately split:
/// <list type="number">
/// <item>This model: given any DiscForge-supported image, describe it as a
/// uniform mountable disc (media type, sessions/tracks, sector count) and
/// serve sectors through <see cref="ISectorSource"/>, regardless of the source
/// format. This is what an emulator consumes.</item>
/// <item>The OS binding (NOT here): exposing that model as a drive letter.
/// On Windows a rich optical emulation needs a kernel-mode storage driver
/// (as Alcohol's and Daemon Tools' signed drivers do). DiscForge does not ship
/// one; that layer is future work. What DiscForge <i>can</i> do without any
/// driver is hand a plain ISO to Windows' own native mount, which this model
/// detects and routes.</item>
/// </list>
///
/// So <see cref="ResolveStrategy"/> answers, honestly, "can this image be
/// mounted right now, and how?": native ISO mount for ISO-compatible images,
/// or "needs the virtual-drive driver" for raw/subchannel/audio formats.
///
/// Nothing here decrypts anything; it mounts unprotected or personally-authored
/// images only.
/// </summary>
public static class VirtualDisc
{
    public enum MediaType { CdRom, DvdRom, BluRay, Unknown }

    public enum MountStrategy
    {
        /// <summary>A plain ISO 9660 / UDF data image: Windows can mount it
        /// natively (Mount-DiskImage / AttachVirtualDisk) with no driver.</summary>
        NativeIso,
        /// <summary>An ISO-compatible data image in a container DiscForge can
        /// export to a plain .iso first, then native-mount.</summary>
        ConvertThenNativeIso,
        /// <summary>Raw / subchannel / audio / multi-track image that needs a
        /// full virtual optical drive (kernel driver) to present faithfully.
        /// DiscForge models it here but cannot bind it to a drive letter yet.</summary>
        NeedsVirtualDriveDriver,
    }

    public sealed record MountableDisc
    {
        public required string SourcePath { get; init; }
        public required MediaType Media { get; init; }
        public required long TotalSectors { get; init; }
        public required int TrackCount { get; init; }
        public required bool HasAudioTracks { get; init; }
        public required bool HasSubchannel { get; init; }
        public required bool IsPlainData { get; init; }   // single data track, cooked 2048
        public required MountStrategy Strategy { get; init; }

        public string Summary => Strategy switch
        {
            MountStrategy.NativeIso =>
                $"{Media} data image, {TotalSectors:N0} sectors — mountable now via Windows native ISO mount.",
            MountStrategy.ConvertThenNativeIso =>
                $"{Media} data image in a raw container — export to .iso, then native-mount.",
            MountStrategy.NeedsVirtualDriveDriver =>
                $"{Media}, {TrackCount} track(s)" +
                (HasAudioTracks ? ", audio" : "") + (HasSubchannel ? ", subchannel" : "") +
                " — faithful mount needs the virtual-drive driver (not yet available).",
            _ => $"{Media}, {TotalSectors:N0} sectors.",
        };
    }

    /// <summary>
    /// Describe how an image would mount, from lightweight facts about it. The
    /// caller supplies what it knows (extension, track/audio/subchannel makeup);
    /// this keeps the model testable without opening every format here.
    /// </summary>
    public static MountableDisc Describe(
        string sourcePath, MediaType media, long totalSectors,
        int trackCount, bool hasAudio, bool hasSubchannel, bool isPlainData)
    {
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();

        MountStrategy strategy;
        if (ext == ".iso" && isPlainData)
            strategy = MountStrategy.NativeIso;
        else if (isPlainData && !hasAudio && !hasSubchannel && trackCount <= 1)
            // A single cooked data track in a .cdi/.bin/.img container: DiscForge
            // can export a plain .iso, which Windows then mounts natively.
            strategy = MountStrategy.ConvertThenNativeIso;
        else
            strategy = MountStrategy.NeedsVirtualDriveDriver;

        return new MountableDisc
        {
            SourcePath = sourcePath,
            Media = media,
            TotalSectors = totalSectors,
            TrackCount = trackCount,
            HasAudioTracks = hasAudio,
            HasSubchannel = hasSubchannel,
            IsPlainData = isPlainData,
            Strategy = strategy,
        };
    }

    /// <summary>The PowerShell that mounts a native-mountable image on Windows —
    /// no driver needed. Returned as text so the CLI can run or print it; the
    /// actual execution is a Windows-side step.</summary>
    public static string NativeMountCommand(string isoPath)
        => $"Mount-DiskImage -ImagePath \"{isoPath}\" -StorageType ISO";

    public static string NativeUnmountCommand(string isoPath)
        => $"Dismount-DiskImage -ImagePath \"{isoPath}\"";

    /// <summary>
    /// Infer media type from the total sector count (rough, size-based): CD up to
    /// ~360k sectors (~846 MB raw), DVD up to ~2.3M, else Blu-ray. Used when the
    /// source doesn't state it.
    /// </summary>
    public static MediaType MediaFromSectors(long totalSectors) => totalSectors switch
    {
        <= 360_000 => MediaType.CdRom,
        <= 2_298_496 => MediaType.DvdRom,
        _ => MediaType.BluRay,
    };
}
