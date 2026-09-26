// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Runtime.Versioning;
using DiscForge.Core.Mmc;
using DiscForge.Core.Rescue;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices.Reading;

/// <summary>
/// A data disc in an optical drive, as a source for <see cref="RescueEngine"/>: 2048-byte sectors
/// read with READ(10). Same clean-room gate as read-disc: a disc that declares copy protection is
/// refused before anything is read, and a copy-protection response mid-read stops the rescue.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DataDiscRescueSource : IRescueSource, IDisposable
{
    private const byte CopyProtectedAsc = 0x6F;
    private const byte MediumNotPresentAsc = 0x3A;

    private readonly SptiDevice _dev;
    private readonly uint _timeout;

    public long SectorCount { get; }
    public int SectorSize => 2048;
    public char DriveLetter { get; }

    /// <param name="timeoutSeconds">How long one read may take before it counts as failed. Short
    /// timeouts keep a rescue moving past bad areas; the drive may still take a while to give up.</param>
    public DataDiscRescueSource(char driveLetter, uint timeoutSeconds = 20)
    {
        DriveLetter = char.ToUpperInvariant(driveLetter);
        _timeout = timeoutSeconds;
        var protection = DataDiscImager.ProtectionReason(DriveLetter);
        if (protection is not null) throw new DiscReadException(protection);
        var cap = DataDiscImager.ReadCapacity(DriveLetter);
        if (cap.BlockLengthBytes != 2048)
            throw new DiscReadException(
                $"This disc reports {cap.BlockLengthBytes}-byte sectors. Rescue copies data discs (2048-byte sectors: " +
                "data CDs, DVDs, Blu-rays). For an audio or mixed-mode CD use Read Disc / read-cdi with --adaptive-reread.");
        if (cap.Sectors == 0) throw new DiscReadException("The drive reports a zero-length disc. Is it blank, or still spinning up?");
        SectorCount = cap.Sectors;
        _dev = new SptiDevice(DriveLetter);
    }

    public bool TryRead(long lba, int count, Span<byte> buffer)
    {
        var r = _dev.SendCommand(MmcCommands.Read10((uint)lba, (ushort)count), buffer[..(count * SectorSize)],
            SptiDataDirection.In, _timeout);
        if (r.Success) return true;
        if (r.Asc == CopyProtectedAsc)
            throw new RescueAbortException($"Stopped at sector {lba:N0}: {r.Describe()}. DiscForge copies unencrypted discs only.");
        if (r.Asc == MediumNotPresentAsc)
            throw new RescueAbortException("The disc was removed (or the drive can no longer see it). The map has been saved — put it back and run the rescue again to continue.");
        return false;
    }

    public void Dispose() => _dev.Dispose();
}
