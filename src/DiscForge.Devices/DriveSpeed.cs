// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Runtime.Versioning;
using DiscForge.Core.Mmc;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices;

/// <summary>A read speed a user can choose, and what to send the drive.</summary>
public sealed record ReadSpeed(string Label, ushort KilobytesPerSecond)
{
    public override string ToString() => Label;
}

/// <summary>
/// Sets a drive's read speed.
///
/// This matters more for recovery than for anything else. A drive spinning at
/// 48x has less than a millisecond to resolve each pit; at 4x it has twelve
/// times as long, and the laser tracks a warped or scratched disc far more
/// steadily. Sectors that fail consistently at full speed often read first time
/// at 4x — the damage hasn't changed, the drive has simply been given time to
/// cope with it.
///
/// The cost is proportional: a full CD at 4x takes about twenty minutes rather
/// than two. So this is offered rather than imposed, and the default stays
/// whatever the drive chose for itself.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DriveSpeed
{
    /// <summary>1x for CD is 176.4 KB/s — 75 sectors a second of 2352 bytes.</summary>
    public const int CdOneX = 176;

    /// <summary>
    /// The speeds worth offering. Not every drive honours every one — most
    /// round to whatever their mechanism supports — but all of them accept the
    /// command, and the drive picking 8x when asked for 4x is still slower than
    /// leaving it alone.
    /// </summary>
    public static IReadOnlyList<ReadSpeed> CdSpeeds { get; } = new[]
    {
        new ReadSpeed("Maximum", 0xFFFF),
        new ReadSpeed("24x", (ushort)(CdOneX * 24)),
        new ReadSpeed("16x", (ushort)(CdOneX * 16)),
        new ReadSpeed("8x", (ushort)(CdOneX * 8)),
        new ReadSpeed("4x — best for damaged discs", (ushort)(CdOneX * 4)),
        new ReadSpeed("2x", (ushort)(CdOneX * 2)),
        new ReadSpeed("1x — slowest, most patient", CdOneX),
    };

    /// <summary>
    /// SET CD SPEED (0xBB). Read speed in bytes 2-3, write speed in 4-5, both
    /// in KB/s big-endian; 0xFFFF means "as fast as you like".
    ///
    /// Failure is deliberately not thrown. A drive that won't set its speed
    /// still reads perfectly well at whatever speed it chose, and refusing to
    /// start a recovery over a rejected preference would be absurd.
    /// </summary>
    public static bool TrySetReadSpeed(char driveLetter, ushort kilobytesPerSecond)
    {
        try
        {
            using var dev = new SptiDevice(driveLetter);
            return TrySetReadSpeed(dev, kilobytesPerSecond);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>As above, on a device already open — so a recovery run doesn't
    /// close and reopen the drive just to change speed.</summary>
    public static bool TrySetReadSpeed(SptiDevice dev, ushort kilobytesPerSecond)
    {
        // One source of truth for the CDB — the unit-tested builder in Core.
        var cdb = SetCdSpeed.BuildCdb(kilobytesPerSecond, SetCdSpeed.Max);

        try
        {
            var r = dev.SendCommand(cdb, Array.Empty<byte>(), SptiDataDirection.None,
                                    timeoutSeconds: 20);
            return r.Success;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Restore the drive to its own choice of speed.</summary>
    public static bool TryResetSpeed(SptiDevice dev) => TrySetReadSpeed(dev, 0xFFFF);
}