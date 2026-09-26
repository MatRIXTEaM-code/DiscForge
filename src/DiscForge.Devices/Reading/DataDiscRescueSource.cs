// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Runtime.Versioning;
using DiscForge.Core.Mmc;
using DiscForge.Core.Recovery;
using DiscForge.Core.Rescue;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices.Reading;

/// <summary>
/// A data disc in an optical drive, as a source for <see cref="RescueEngine"/>. Beyond plain READ(10):
/// <list type="bullet">
/// <item>Single-sector reads in damaged areas set Force Unit Access, so the drive reads the disc
/// again rather than answering from its cache.</item>
/// <item>Retries first read a sector far away, pushing the damaged area out of the drive's read-ahead
/// cache (some drives ignore FUA), and then read with FUA.</item>
/// <item>On a CD, a sector that still fails is read raw with C2 error pointers several times; the
/// good bytes of each attempt are combined and anything left is repaired from the sector's own
/// Reed-Solomon parity. The result is only accepted if the sector's EDC and ECC check out.</item>
/// <item>Only if asked (<see cref="RescueOptions.SalvageUnverified"/>): for sectors that never read, the
/// drive's uncorrected data — READ(12) with the Streaming bit on DVD/BD, the best C2 vote on a CD —
/// instead of zeros. Such sectors stay marked bad.</item>
/// <item>The drive is slowed down for the damaged areas (<see cref="IRescueSpeedControl"/>) and put
/// back to full speed afterwards.</item>
/// </list>
/// Same clean-room gate as read-disc: a disc that declares copy protection is refused before anything
/// is read, and a copy-protection response mid-read stops the rescue.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DataDiscRescueSource : IRescueSource, IRescueSpeedControl, IRescueSalvage, IDisposable
{
    private const byte CopyProtectedAsc = 0x6F;
    private const byte MediumNotPresentAsc = 0x3A;

    private readonly SptiDevice _dev;
    private readonly uint _timeout;
    private readonly byte[] _scratch = new byte[2048];
    private readonly C2ReadOptions _c2Options;

    public long SectorCount { get; }
    public int SectorSize => 2048;
    public char DriveLetter { get; }

    /// <summary>True for a CD, where raw reads with C2 pointers are available.</summary>
    public bool IsCd { get; }
    /// <summary>Whether the drive returned C2 pointers when asked (CD only).</summary>
    public bool C2Available { get; }
    /// <summary>"CD", "DVD" or "Blu-ray" (by size, for the careful read speed).</summary>
    public string MediaKind { get; }

    /// <summary>Sectors rebuilt from C2-guided raw reads (and parity) that READ(10) couldn't deliver.</summary>
    public int C2Recovered { get; private set; }

    /// <param name="timeoutSeconds">How long one read may take before it counts as failed.</param>
    /// <param name="useC2">Use C2-guided raw reads for failing CD sectors.</param>
    /// <param name="c2Reads">Raw reads per sector when combining with C2.</param>
    public DataDiscRescueSource(char driveLetter, uint timeoutSeconds = 20, bool useC2 = true, int c2Reads = 8)
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

        // READ CD only works on CD media, so a successful C2 read of the volume descriptor area says
        // both "this is a CD" and "the drive gives C2 pointers".
        IsCd = SectorCount <= 450_000 && C2SectorReader.SupportsC2(_dev, (uint)Math.Min(16, SectorCount - 1));
        C2Available = IsCd && useC2;
        MediaKind = IsCd || SectorCount <= 450_000 ? "CD" : SectorCount <= 4_200_000 ? "DVD" : "Blu-ray";
        _c2Options = new C2ReadOptions { MaxReads = Math.Clamp(c2Reads, 1, 64), UseEccCorrection = true };
    }

    public bool TryRead(long lba, int count, Span<byte> buffer, RescueReadKind kind)
    {
        var dest = buffer[..(count * SectorSize)];
        switch (kind)
        {
            case RescueReadKind.Bulk:
                return Check(_dev.SendCommand(MmcCommands.Read10((uint)lba, (ushort)count), dest, SptiDataDirection.In, _timeout), lba);

            case RescueReadKind.Retry:
                // Move the read-ahead cache off this area first: read something far away.
                long far = lba < SectorCount / 2 ? SectorCount - 1 : 0;
                _dev.SendCommand(MmcCommands.Read10((uint)far, 1), _scratch, SptiDataDirection.In, _timeout);
                goto default;

            default:
                if (Check(_dev.SendCommand(Read10Fua((uint)lba, (ushort)count), dest, SptiDataDirection.In, _timeout), lba))
                    return true;
                // Scraping and retrying on a CD: combine C2-guided raw reads.
                if (C2Available && count == 1 && kind is RescueReadKind.Single or RescueReadKind.Retry)
                    return TryC2(lba, dest);
                return false;
        }
    }

    /// <summary>READ(10) with Force Unit Access (byte 1 bit 3): read the medium, not the cache.</summary>
    private static byte[] Read10Fua(uint lba, ushort count)
    {
        var cdb = MmcCommands.Read10(lba, count);
        cdb[1] |= 0x08;
        return cdb;
    }

    private bool Check(SptiResult r, long lba)
    {
        if (r.Success) return true;
        if (r.Asc == CopyProtectedAsc)
            throw new RescueAbortException($"Stopped at sector {lba:N0}: {r.Describe()}. DiscForge copies unencrypted discs only.");
        if (r.Asc == MediumNotPresentAsc)
            throw new RescueAbortException("The disc was removed (or the drive can no longer see it). The map has been saved — put it back and run the rescue again to continue.");
        return false;
    }

    private bool TryC2(long lba, Span<byte> dest)
    {
        C2SectorResult res;
        try { res = C2SectorReader.ReadSector(_dev, (uint)lba, _c2Options); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { return false; }
        if (!res.Complete || res.Sector.Length < 2352) return false;
        var raw = res.Sector.AsSpan(0, 2352);
        // Accept only what the sector's own EDC and ECC vouch for.
        if (!RereadEvidence.CheckDataEdc(raw)) return false;
        int offset = raw[15] == 1 ? 16 : 24;   // Mode 1 / Mode 2 Form 1 user data
        raw.Slice(offset, 2048).CopyTo(dest);
        C2Recovered++;
        return true;
    }

    public bool TrySalvage(long lba, Span<byte> buffer)
    {
        var dest = buffer[..SectorSize];
        if (IsCd && C2Available)
        {
            try
            {
                var res = C2SectorReader.ReadSector(_dev, (uint)lba, _c2Options);
                if (res.AllReadsRefused || res.Sector.Length < 2352) return false;
                int offset = res.Sector[15] == 2 ? 24 : 16;
                res.Sector.AsSpan(offset, 2048).CopyTo(dest);
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { return false; }
        }
        // Streaming READ(12): standard MMC; the drive returns the sector even when it can't correct it.
        var r = _dev.SendCommand(MmcCommands.Read12((uint)lba, 1, streaming: true), dest, SptiDataDirection.In, _timeout);
        if (r.Asc == CopyProtectedAsc)
            throw new RescueAbortException($"Stopped at sector {lba:N0}: {r.Describe()}. DiscForge copies unencrypted discs only.");
        return r.Success;
    }

    /// <summary>Careful = a low read speed suited to the media; otherwise the drive's maximum.</summary>
    public void SetCareful(bool careful)
    {
        // SET CD SPEED takes kB/s for every media type. 1x: CD 176, DVD 1,385, BD 4,495 kB/s.
        ushort kbps = !careful ? (ushort)0xFFFF : MediaKind switch
        {
            "CD" => (ushort)706,      // 4x
            "DVD" => (ushort)2770,    // 2x
            _ => (ushort)4495,        // 1x
        };
        try { _dev.SendCommand(MmcCommands.SetCdSpeed(kbps, 0xFFFF), Span<byte>.Empty, SptiDataDirection.None, 15); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { /* not all drives accept it */ }
    }

    public void Dispose() => _dev.Dispose();
}
