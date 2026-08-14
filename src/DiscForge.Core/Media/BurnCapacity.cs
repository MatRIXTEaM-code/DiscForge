// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Media;

/// <summary>How an image's size compares to the target media's capacity.</summary>
public enum CapacityFit
{
    /// <summary>Fits within the disc's nominal capacity.</summary>
    Fits,
    /// <summary>Smaller than the disc — normal (an underburn, i.e. blank space left over).</summary>
    Underburn,
    /// <summary>Larger than nominal but within the drive's over-capacity tolerance —
    /// possible only if the drive and media allow overburning.</summary>
    Overburn,
    /// <summary>Too large even for overburn — cannot be written to this media.</summary>
    TooLarge,
}

/// <summary>The result of checking an image against target media capacity.</summary>
public sealed record CapacityCheck
{
    public required CapacityFit Fit { get; init; }
    public required long ImageSectors { get; init; }
    public required long MediaSectors { get; init; }
    /// <summary>Sectors past nominal capacity (0 unless an overburn/too-large).</summary>
    public long OverburnSectors => Math.Max(0, ImageSectors - MediaSectors);
    /// <summary>Free sectors left on the disc (0 unless an underburn).</summary>
    public long FreeSectors => Math.Max(0, MediaSectors - ImageSectors);
    /// <summary>True when the burn may proceed under the supplied options.</summary>
    public required bool CanBurn { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// Checks whether an image fits the target optical media, and decides whether an
/// over-capacity image may still be burned (overburn). Pure arithmetic over sector
/// counts — no hardware, fully testable. The engine calls this before a burn so an
/// oversize image is refused (or explicitly overburned) up front rather than failing
/// partway through a disc.
///
/// Capacities are expressed in the media's own 2048-byte sectors. Reference nominal
/// capacities are provided for convenience; a caller that has read the real
/// READ CAPACITY / disc information from the drive should pass that instead.
/// </summary>
public static class BurnCapacity
{
    /// <summary>Nominal usable capacity, in 2048-byte sectors, for common media.</summary>
    public static class Nominal
    {
        public const long Cd74 = 333_000;      // 650 MB
        public const long Cd80 = 359_849;      // 700 MB
        public const long Dvd5 = 2_298_496;    // 4.7 GB single layer
        public const long Dvd9 = 4_173_824;    // 8.5 GB dual layer
        public const long Bd25 = 11_826_176;   // 25 GB single layer
        public const long Bd50 = 23_652_352;   // 50 GB dual layer
        public const long Bd100 = 48_878_592;  // 100 GB BDXL triple layer
    }

    /// <summary>
    /// Compare an image (in 2048-byte sectors) to media capacity.
    /// </summary>
    /// <param name="allowOverburn">Permit writing past nominal capacity when the drive
    /// supports it (ImgBurn's "overburn" toggle).</param>
    /// <param name="overburnTolerance">Fraction of nominal capacity the drive/media can
    /// exceed (e.g. 0.05 = 5%). Overburn beyond this is refused as too large.</param>
    public static CapacityCheck Check(long imageSectors, long mediaSectors,
                                      bool allowOverburn = false, double overburnTolerance = 0.05)
    {
        if (imageSectors < 0) throw new ArgumentOutOfRangeException(nameof(imageSectors));
        if (mediaSectors <= 0) throw new ArgumentOutOfRangeException(nameof(mediaSectors));

        if (imageSectors <= mediaSectors)
        {
            bool exact = imageSectors == mediaSectors;
            long free = mediaSectors - imageSectors;
            return new CapacityCheck
            {
                Fit = exact ? CapacityFit.Fits : CapacityFit.Underburn,
                ImageSectors = imageSectors,
                MediaSectors = mediaSectors,
                CanBurn = true,
                Message = exact
                    ? "Image exactly fills the disc."
                    : $"Fits with {SectorsToMb(free):0.0} MB ({free:N0} sectors) to spare.",
            };
        }

        long over = imageSectors - mediaSectors;
        long maxOver = (long)(mediaSectors * overburnTolerance);
        if (allowOverburn && over <= maxOver)
        {
            return new CapacityCheck
            {
                Fit = CapacityFit.Overburn,
                ImageSectors = imageSectors,
                MediaSectors = mediaSectors,
                CanBurn = true,
                Message = $"Overburn: {SectorsToMb(over):0.0} MB ({over:N0} sectors) past nominal " +
                          "capacity — only some drive/media combinations accept this; verify the result.",
            };
        }

        return new CapacityCheck
        {
            Fit = over <= maxOver ? CapacityFit.Overburn : CapacityFit.TooLarge,
            ImageSectors = imageSectors,
            MediaSectors = mediaSectors,
            CanBurn = false,
            Message = allowOverburn
                ? $"Too large even to overburn: {SectorsToMb(over):0.0} MB over, beyond the " +
                  $"{overburnTolerance:P0} tolerance."
                : $"Image is {SectorsToMb(over):0.0} MB larger than the disc. Enable overburn to " +
                  "attempt it (drive/media permitting), or use larger media.",
        };
    }

    private static double SectorsToMb(long sectors) => sectors * 2048.0 / (1024 * 1024);
}
