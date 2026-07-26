// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Cdi;
using DiscForge.Core.Devices;

namespace DiscForge.Core.Burning;

/// <summary>The write strategy a burn will use, chosen from disc content and
/// drive capability — not assumed.</summary>
public enum BurnMethod
{
    /// <summary>Standard data/ISO/BD burn via the OS stack (IMAPI2 Data,
    /// session-at-once). No RAW, no subchannel; works on any modern drive.
    /// Single data track.</summary>
    Imapi2Data,

    /// <summary>
    /// Audio CD written track-at-once via the OS stack (IMAPI2 TrackAtOnce) —
    /// how Windows itself burns audio CDs, and it works on ANY CD writer.
    ///
    /// TAO inserts the standard two-second gap between tracks, so it cannot
    /// reproduce a disc's exact gaps (gapless mixes, or a byte-faithful copy).
    /// Those need RAW DAO.
    /// </summary>
    Imapi2TrackAtOnce,

    /// <summary>Raw DAO-96 via native SPTI MMC — full sector + subchannel, for
    /// byte-faithful mixed/multisession CD burns, exact gaps, CD-TEXT and CD+G.
    /// Needs a capable drive.</summary>
    RawDao96,
}

/// <summary>Progress callback payload.</summary>
public readonly record struct BurnProgress(string Phase, double Fraction, string? Detail = null);

/// <summary>
/// A validated plan to burn a specific image on a specific drive. Produced by
/// <see cref="BurnPlanner"/>, consumed by an <see cref="IBurnEngine"/>. Keeps the
/// "can this drive do this?" decision in pure, testable code, separate from the
/// platform burn implementation.
/// </summary>
public sealed record BurnPlan
{
    public required BurnMethod Method { get; init; }
    public required string DevicePath { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>
    /// Requested write speed in sectors per second (IMAPI2's native unit:
    /// CD 1x = 75, DVD 1x ≈ 677, BD 1x ≈ 2195). Null means "maximum" — the
    /// drive's own default. Engines treat this as a request, not a promise:
    /// a drive that can't do the exact speed picks its closest supported one,
    /// and a drive that rejects the call entirely still burns at max rather
    /// than failing the job over a speed preference.
    /// </summary>
    public int? WriteSpeedSectorsPerSecond { get; init; }
}

/// <summary>Raised when an image cannot be burned on the chosen drive.</summary>
public sealed class BurnNotSupportedException(string message) : Exception(message);

/// <summary>
/// The only facts a burn method depends on. Letting the planner work from this
/// rather than a whole <see cref="CdiImage"/> means a job can be validated before
/// the image exists — which is what makes it possible to tell someone their
/// burner can't write a disc BEFORE spending ten minutes reading one.
/// </summary>
/// <param name="NonStandardGaps">
/// True when any track's pregap differs from the customary 150 sectors (two
/// seconds) — a gapless mix, or a copy whose gaps must be reproduced exactly.
/// TAO always writes the standard gap, so such a disc needs RAW DAO to be
/// faithful.
/// </param>
public sealed record ImageShape(
    int TrackCount, int SessionCount, bool HasAudio, bool HasData = false, bool NonStandardGaps = false)
{
    /// <summary>Every track is audio — a plain audio CD.</summary>
    public bool AllAudio => HasAudio && !HasData;
    /// <summary>Audio and data on one disc.</summary>
    public bool Mixed => HasAudio && HasData;

    public static ImageShape Of(CdiImage image)
    {
        var tracks = image.AllTracks.ToList();
        bool audio = tracks.Any(t => t.Mode == CdiTrackMode.Audio);
        bool data = tracks.Any(t => t.Mode != CdiTrackMode.Audio);

        // Track 1 customarily carries the 150-sector lead-in gap; later tracks
        // 150 as well. Anything else can't survive TAO.
        bool oddGaps = tracks.Any(t => t.PregapSectors is not (0 or 150));
        if (audio && tracks.Skip(1).Any(t => t.PregapSectors == 0)) oddGaps = true;  // gapless

        return new ImageShape(image.TrackCount, image.Sessions.Count, audio, data, oddGaps);
    }
}

/// <summary>
/// Decides whether and how a given image can be burned on a given drive. Pure
/// logic over the capability model — fully unit-testable, no hardware.
/// </summary>
public static class BurnPlanner
{
    public static BurnPlan Plan(CdiImage image, DriveCapabilities drive)
        => Plan(ImageShape.Of(image), drive);

    public static BurnPlan Plan(ImageShape shape, DriveCapabilities drive)
    {
        var warnings = new List<string>();
        bool multisession = shape.SessionCount > 1;
        bool anyWriter = drive.CdWrite || drive.DvdWrite || drive.BdWrite;

        // 1) A plain single data track: the OS data path, on any writer.
        if (shape.TrackCount == 1 && !shape.HasAudio && !multisession)
        {
            if (!anyWriter)
                throw new BurnNotSupportedException($"{drive.Vendor} {drive.Model} is not a writer.");
            return new BurnPlan
            {
                Method = BurnMethod.Imapi2Data,
                DevicePath = drive.DevicePath,
                Warnings = warnings,
            };
        }

        // 2) A plain audio CD: track-at-once via the OS stack. This is how
        //    Windows itself burns audio, and it works on ANY CD writer — no RAW
        //    DAO required. Only exact gaps (a gapless mix, or a byte-faithful
        //    copy) force RAW.
        if (shape.AllAudio && !multisession)
        {
            if (!drive.CdWrite)
                throw new BurnNotSupportedException(
                    $"{drive.Vendor} {drive.Model} cannot write CDs, and audio is a CD format.");

            if (!shape.NonStandardGaps)
            {
                warnings.Add("Audio written track-at-once: the standard two-second gap is placed " +
                             "between tracks.");
                return new BurnPlan
                {
                    Method = BurnMethod.Imapi2TrackAtOnce,
                    DevicePath = drive.DevicePath,
                    Warnings = warnings,
                };
            }

            // Non-standard gaps: TAO would silently standardise them.
            if (drive.RawDao96)
            {
                warnings.Add("Audio has non-standard gaps, so RAW DAO is used to reproduce them exactly.");
                return new BurnPlan
                {
                    Method = BurnMethod.RawDao96,
                    DevicePath = drive.DevicePath,
                    Warnings = warnings,
                };
            }

            throw new BurnNotSupportedException(
                $"This audio has non-standard gaps (gapless, or gaps copied from a source disc), " +
                $"which need RAW DAO-96 to reproduce. {drive.Vendor} {drive.Model} does not support " +
                "RAW DAO. It CAN write the audio track-at-once, but every gap would become the " +
                "standard two seconds — choose TAO explicitly if that's acceptable.");
        }

        // 3) Multisession: RAW DAO via the OS stack writes ONE session and
        //    closes the disc — reproducing session gaps needs the native SPTI
        //    engine, which is future work. Saying so beats burning a wrong disc.
        if (multisession)
            throw new BurnNotSupportedException(
                $"This image has {shape.SessionCount} sessions. RAW disc-at-once writing " +
                "through the OS stack produces a single closed session, so a multisession " +
                "image cannot be reproduced faithfully yet. (Single-session images — " +
                "including mixed-mode — are fully supported.)");

        // 4) Mixed-mode or multi-track single-session: the RAW DAO engine
        //    composes the whole disc (every gap, index and sub-channel) itself.
        if (shape.Mixed || shape.TrackCount > 1)
        {
            if (!drive.CdWrite)
                throw new BurnNotSupportedException(
                    "Image needs CD writing but the drive can't write CD.");
            // The mode-page 2A raw bit (RawDao96) under-reports what IMAPI2's
            // raw writer can actually do — real drives (the TSSTcorp SE-208DB
            // among them) say False here yet negotiate a raw sector type fine.
            // So a clear bit is a caution, not a refusal: the engine asks the
            // drive for real at PrepareMedia and fails cleanly if the answer
            // is genuinely no.
            if (!drive.RawDao96)
                warnings.Add($"{drive.Vendor} {drive.Model} doesn't advertise RAW DAO-96 in its " +
                             "mode page. IMAPI2 will be asked directly at burn time; if the " +
                             "drive truly can't write raw, the burn stops before touching the disc.");
            if (shape.Mixed)
                warnings.Add("Mixed-mode RAW burn: verify the result on target hardware.");
            return new BurnPlan
            {
                Method = BurnMethod.RawDao96,
                DevicePath = drive.DevicePath,
                Warnings = warnings,
            };
        }

        throw new BurnNotSupportedException("Could not determine a burn method for this image.");
    }
}

/// <summary>A platform burn implementation. Implemented in DiscForge.Devices
/// (IMAPI2 now; SPTI RAW DAO later).</summary>
public interface IBurnEngine
{
    bool Supports(BurnMethod method);

    /// <summary>Burn the image per the plan, reporting progress. Throws on
    /// hardware/media errors. Implementations must never fabricate success.</summary>
    void Burn(Stream cdi, CdiImage image, BurnPlan plan, IProgress<BurnProgress>? progress = null);
}
