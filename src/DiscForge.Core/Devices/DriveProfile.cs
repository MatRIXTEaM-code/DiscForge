// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using System.Text.Json;

namespace DiscForge.Core.Devices;

/// <summary>How well a capability is actually known — kept explicit so the profile never dresses
/// an unmeasured property up as a measured one.</summary>
public enum ProbeState
{
    /// <summary>Measured true.</summary>
    Yes,
    /// <summary>Measured / advertised false.</summary>
    No,
    /// <summary>The drive claims it (INQUIRY / GET CONFIGURATION / mode page 2Ah), not empirically verified.</summary>
    Advertised,
    /// <summary>An empirical probe would settle it, but that probe isn't implemented.</summary>
    NotProbed,
    /// <summary>Needs an external reference (a calibration / known-defective disc) that wasn't supplied.</summary>
    NotDetermined,
}

/// <summary>
/// A consolidated, one-shot description of an optical drive: its read/write reach, write modes and
/// read-fidelity features, gathered from <see cref="DriveCapabilities"/> (INQUIRY + MMC GET
/// CONFIGURATION + mode page 2Ah), plus the empirical fidelity probes a Redump-grade dumper cares
/// about — C2 accuracy, lead-out overread, cache-defeat, and the drive's audio read offset.
///
/// The advertised half is real and complete. The empirical half is reported HONESTLY: C2 accuracy
/// needs a known-defective disc to validate, overread and cache-defeat need hardware timing probes
/// that aren't implemented, and the read offset needs a known-offset AccurateRip reference — so
/// each is carried as an explicit <see cref="ProbeState"/> (<c>NotProbed</c> / <c>NotDetermined</c>)
/// rather than a fabricated number. That is the whole point of this type: a drive profile you can
/// trust field-by-field, in the same "provably correct or declined" spirit as the rest of DiscForge.
/// This class is pure and unit-tested; the live capability read lives in the CLI (Windows SPTI).
/// </summary>
public sealed record DriveProfile
{
    public required string DevicePath { get; init; }
    public required string Vendor { get; init; }
    public required string Model { get; init; }
    public required string Firmware { get; init; }

    public bool CdRead { get; init; }
    public bool CdWrite { get; init; }
    public bool DvdRead { get; init; }
    public bool DvdWrite { get; init; }
    public bool BdRead { get; init; }
    public bool BdWrite { get; init; }

    public bool TrackAtOnce { get; init; }
    public bool DiscAtOnce { get; init; }             // SAO/DAO (CD mastering)
    public bool RawDao96 { get; init; }
    public bool RawReadSubchannel { get; init; }
    public bool BufferUnderrunProtection { get; init; }

    // Empirical fidelity — advertised vs actually probed, never conflated.
    public ProbeState C2ErrorPointers { get; init; }  // Advertised / No — from mode page 2Ah
    public ProbeState C2Accuracy { get; init; }       // needs a known-defective disc → NotDetermined
    public ProbeState Overread { get; init; }         // needs a hardware probe → NotProbed
    public ProbeState CacheDefeat { get; init; }      // needs a timing probe → NotProbed
    public int? ReadOffsetSamples { get; init; }      // null → not determined (needs a reference)

    public string MediaProfile { get; init; } = "None";

    /// <summary>Build a profile from a live capability read. <paramref name="readOffsetSamples"/>
    /// is filled only when a caller has determined it (e.g. from an AccurateRip reference);
    /// otherwise the profile honestly leaves the offset undetermined.</summary>
    public static DriveProfile FromCapabilities(DriveCapabilities caps, int? readOffsetSamples = null,
                                                ProbeState overread = ProbeState.NotProbed)
    {
        ArgumentNullException.ThrowIfNull(caps);
        return new DriveProfile
        {
            DevicePath = caps.DevicePath,
            Vendor = caps.Vendor,
            Model = caps.Model,
            Firmware = caps.FirmwareRevision,

            CdRead = caps.CdRead, CdWrite = caps.CdWrite,
            DvdRead = caps.DvdRead, DvdWrite = caps.DvdWrite,
            BdRead = caps.BdRead, BdWrite = caps.BdWrite,

            TrackAtOnce = caps.TrackAtOnce,
            DiscAtOnce = caps.DiscAtOnce,
            RawDao96 = caps.RawDao96,
            RawReadSubchannel = caps.RawReadSubchannel,
            BufferUnderrunProtection = caps.BufferUnderrunProtection,

            C2ErrorPointers = caps.C2ErrorReporting ? ProbeState.Advertised : ProbeState.No,
            // C2 pointers being advertised says nothing about whether they're ACCURATE — only a
            // disc with known-bad sectors proves that. Leave it undetermined unless advertised-false.
            C2Accuracy = caps.C2ErrorReporting ? ProbeState.NotDetermined : ProbeState.No,
            Overread = overread,
            CacheDefeat = ProbeState.NotProbed,
            ReadOffsetSamples = readOffsetSamples,

            MediaProfile = caps.MediaProfile.ToString(),
        };
    }

    private static string Yn(bool b) => b ? "yes" : "no";

    private static string P(ProbeState s) => s switch
    {
        ProbeState.Yes => "yes",
        ProbeState.No => "no",
        ProbeState.Advertised => "advertised",
        ProbeState.NotProbed => "not probed",
        ProbeState.NotDetermined => "not determined",
        _ => s.ToString(),
    };

    /// <summary>Human-readable per-drive profile.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Vendor} {Model} (fw {Firmware})  [{DevicePath}]");
        sb.AppendLine($"  media        : {MediaProfile}");
        sb.AppendLine($"  read         : CD {Yn(CdRead)}, DVD {Yn(DvdRead)}, BD {Yn(BdRead)}");
        sb.AppendLine($"  write        : CD {Yn(CdWrite)}, DVD {Yn(DvdWrite)}, BD {Yn(BdWrite)}");
        sb.AppendLine($"  write modes  : TAO {Yn(TrackAtOnce)}, DAO/SAO {Yn(DiscAtOnce)}, RAW-DAO-96 {Yn(RawDao96)}");
        sb.AppendLine( "  read fidelity:");
        sb.AppendLine($"    raw sub-channel read : {Yn(RawReadSubchannel)}");
        sb.AppendLine($"    buffer-underrun prot : {Yn(BufferUnderrunProtection)}");
        sb.AppendLine($"    C2 error pointers    : {P(C2ErrorPointers)}");
        sb.AppendLine($"    C2 accuracy          : {P(C2Accuracy)}  (needs a known-defective disc to validate)");
        sb.AppendLine($"    overread (lead-out)  : {P(Overread)}  (single-sector lead-out probe; needs a disc loaded)");
        sb.AppendLine($"    cache defeat         : {P(CacheDefeat)}  (timing probe not implemented)");
        sb.AppendLine($"    read offset (samples): {(ReadOffsetSamples is int o ? o.ToString() : "not determined")}" +
                       "  (run `read-offset` against a known-offset AccurateRip reference)");
        sb.Append(     "  note: advertised flags come from INQUIRY + MMC GET CONFIGURATION + mode page 2Ah; the " +
                       "empirical probes above need a calibration/defective disc and are reported unprobed, not guessed.");
        return sb.ToString();
    }

    /// <summary>Machine-readable profile (stable field names) for saving a per-drive record.</summary>
    public string Json() => JsonSerializer.Serialize(new
    {
        devicePath = DevicePath,
        vendor = Vendor,
        model = Model,
        firmware = Firmware,
        mediaProfile = MediaProfile,
        read = new { cd = CdRead, dvd = DvdRead, bd = BdRead },
        write = new { cd = CdWrite, dvd = DvdWrite, bd = BdWrite },
        writeModes = new { trackAtOnce = TrackAtOnce, discAtOnce = DiscAtOnce, rawDao96 = RawDao96 },
        readFidelity = new
        {
            rawReadSubchannel = RawReadSubchannel,
            bufferUnderrunProtection = BufferUnderrunProtection,
            c2ErrorPointers = C2ErrorPointers.ToString(),
            c2Accuracy = C2Accuracy.ToString(),
            overread = Overread.ToString(),
            cacheDefeat = CacheDefeat.ToString(),
            readOffsetSamples = ReadOffsetSamples,
        },
    }, new JsonSerializerOptions { WriteIndented = true });
}
