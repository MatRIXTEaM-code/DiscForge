// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cdi;
using DiscForge.Core.Devices;

namespace DiscForge.Core.Burning;

/// <summary>
/// What the user asked for in the Methods box. Mirrors the classic burner UI:
/// tick nothing and you get DAO/SAO (whatever the planner decides from the
/// content and the drive); tick TAO or RAW to force a specific strategy.
/// </summary>
public enum BurnMethodChoice
{
    /// <summary>Let the planner choose from content + drive capability (DAO/SAO).</summary>
    Auto,
    /// <summary>Track-at-once.</summary>
    Tao,
    /// <summary>Force RAW DAO-96 (needs a capable drive).</summary>
    RawDao96,
}

/// <summary>
/// Where a job writes to: a physical drive, or an image file on disk. A file
/// destination is first-class — the classic tools list `image.cdi` in the same
/// destination box as the hardware, and it's genuinely useful.
/// </summary>
public abstract record BurnDestination
{
    public sealed record Drive(DriveCapabilities Capabilities) : BurnDestination;
    public sealed record ImageFile(string Path) : BurnDestination;
}

/// <summary>One unit of work in a job.</summary>
public enum BurnStepKind { Test, Write, Verify }

/// <summary>A single planned step: what to do, how, and for which copy.</summary>
public sealed record BurnStep(BurnStepKind Kind, BurnMethod Method, int CopyNumber);

/// <summary>A job as requested by the user, before validation.</summary>
public sealed record BurnJob
{
    public required BurnDestination Destination { get; init; }
    /// <summary>Simulated burn (laser off) before writing for real.</summary>
    public bool Test { get; init; }
    public bool Write { get; init; } = true;
    /// <summary>Read the result back and compare against the source.</summary>
    public bool Verify { get; init; }
    public int Copies { get; init; } = 1;
    public BurnMethodChoice Method { get; init; } = BurnMethodChoice.Auto;
}

/// <summary>A validated job: an ordered list of steps plus context.</summary>
public sealed record BurnJobPlan
{
    public required IReadOnlyList<BurnStep> Steps { get; init; }
    public required string DestinationLabel { get; init; }
    public required bool IsImageFile { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    public int TotalCopies => Steps.Count == 0 ? 0 : Steps.Max(s => s.CopyNumber);
}

/// <summary>
/// One destination's outcome when planning a job across several at once.
/// Either it has steps, or it has a <see cref="Refusal"/> saying why not —
/// one incapable drive must not sink the whole job, it just doesn't take part.
/// </summary>
public sealed record DestinationPlan
{
    public required BurnDestination Destination { get; init; }
    public required string Label { get; init; }
    public required bool IsImageFile { get; init; }
    /// <summary>Null when this destination can run; otherwise the reason it can't.</summary>
    public string? Refusal { get; init; }
    public IReadOnlyList<BurnStep> Steps { get; init; } = Array.Empty<BurnStep>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool CanRun => Refusal is null;
    public int TotalCopies => Steps.Count == 0 ? 0 : Steps.Max(s => s.CopyNumber);
}

/// <summary>A job planned across every chosen destination.</summary>
public sealed record MultiBurnPlan
{
    public required IReadOnlyList<DestinationPlan> Destinations { get; init; }

    public IEnumerable<DestinationPlan> Runnable => Destinations.Where(d => d.CanRun);
    public IEnumerable<DestinationPlan> Refused => Destinations.Where(d => !d.CanRun);
    public bool AnyRunnable => Runnable.Any();
}

/// <summary>A job requested against several destinations at once.</summary>
public sealed record MultiBurnJob
{
    public required IReadOnlyList<BurnDestination> Destinations { get; init; }
    public bool Test { get; init; }
    public bool Write { get; init; } = true;
    public bool Verify { get; init; }
    public int Copies { get; init; } = 1;
    public BurnMethodChoice Method { get; init; } = BurnMethodChoice.Auto;

/// <summary>
    /// The job as it applies to one destination.
    ///
    /// Copies and Test are disc concepts. In a job spanning drives and an
    /// archive file, the drives make their copies while the file is written
    /// once — refusing the archive because two discs were asked for would be
    /// unhelpful, and dropping it silently would be worse. Asking for either
    /// against a file destination *on its own* is still refused, because then
    /// it's the whole request rather than one part of a wider one.
    /// </summary>
    public BurnJob For(BurnDestination destination)
    {
        bool isFile = destination is BurnDestination.ImageFile;
        return new BurnJob
        {
            Destination = destination,
            Test = isFile ? false : Test,
            Write = Write,
            Verify = Verify,
            Copies = isFile ? 1 : Copies,
            Method = Method,
        };
    }
}
/// <summary>
/// Validates and expands a <see cref="BurnJob"/> into ordered steps. Pure logic
/// over the capability model — no hardware, fully unit-testable, which is the
/// whole point: the risky part of burning is deciding what to attempt, and a job
/// that cannot be honoured should be refused before any media is touched, not
/// discovered half way through.
///
/// Ordering: an optional Test runs once up front (simulating the same write
/// repeatedly buys nothing), then Write and Verify run per copy.
/// </summary>
public static class BurnJobPlanner
{
    /// <summary>
    /// Plan a job across several destinations at once — the classic "burn to
    /// every drive simultaneously" duplication case.
    ///
    /// Each destination is planned independently and may be refused on its own
    /// terms: a drive that can't take a RAW image simply doesn't take part, and
    /// says why, while the capable ones still run. A job is only impossible if
    /// *nothing* can run.
    /// </summary>
    public static MultiBurnPlan PlanAll(CdiImage image, MultiBurnJob job)
        => PlanAll(ImageShape.Of(image), job);

    /// <summary>Plan from an image's shape rather than the image itself — lets a
    /// copy be validated before the intermediate image has been read.</summary>
    public static MultiBurnPlan PlanAll(ImageShape shape, MultiBurnJob job)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(job);

        if (job.Destinations.Count == 0)
            throw new BurnNotSupportedException("Choose at least one destination.");
        if (!job.Test && !job.Write && !job.Verify)
            throw new BurnNotSupportedException("Select at least one action (Test, Write or Verify).");
        if (job.Copies < 1)
            throw new BurnNotSupportedException("Copies must be at least 1.");

        // Writing the same file twice in one job would have them fight over it.
        var files = job.Destinations.OfType<BurnDestination.ImageFile>()
            .GroupBy(f => Path.GetFullPath(f.Path), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (files.Count > 0)
            throw new BurnNotSupportedException(
                $"The same image file is listed more than once: {string.Join(", ", files)}.");

        var drives = job.Destinations.OfType<BurnDestination.Drive>()
            .GroupBy(d => d.Capabilities.DevicePath, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (drives.Count > 0)
            throw new BurnNotSupportedException(
                $"The same drive is listed more than once: {string.Join(", ", drives)}.");

        var results = new List<DestinationPlan>(job.Destinations.Count);
        foreach (var destination in job.Destinations)
        {
            string label = Describe(destination);
            bool isFile = destination is BurnDestination.ImageFile;
            try
            {
                var one = Plan(shape, job.For(destination));
                results.Add(new DestinationPlan
                {
                    Destination = destination,
                    Label = one.DestinationLabel,
                    IsImageFile = one.IsImageFile,
                    Steps = one.Steps,
                    Warnings = one.Warnings,
                });
            }
            catch (BurnNotSupportedException ex)
            {
                // Refused on its own terms — the others carry on.
                results.Add(new DestinationPlan
                {
                    Destination = destination,
                    Label = label,
                    IsImageFile = isFile,
                    Refusal = ex.Message,
                });
            }
        }

        var plan = new MultiBurnPlan { Destinations = results };
        if (!plan.AnyRunnable)
            throw new BurnNotSupportedException(
                "No destination can run this job:" + Environment.NewLine +
                string.Join(Environment.NewLine,
                    plan.Refused.Select(d => $"  {d.Label}: {d.Refusal}")));

        return plan;
    }

    private static string Describe(BurnDestination destination) => destination switch
    {
        BurnDestination.Drive d => $"{d.Capabilities.Vendor} {d.Capabilities.Model} ({d.Capabilities.DevicePath})",
        BurnDestination.ImageFile f => f.Path,
        _ => "unknown destination",
    };

    public static BurnJobPlan Plan(CdiImage image, BurnJob job)
        => Plan(ImageShape.Of(image), job);

    public static BurnJobPlan Plan(ImageShape shape, BurnJob job)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(job);

        if (!job.Test && !job.Write && !job.Verify)
            throw new BurnNotSupportedException("Select at least one action (Test, Write or Verify).");
        if (job.Copies < 1)
            throw new BurnNotSupportedException("Copies must be at least 1.");

        return job.Destination switch
        {
            BurnDestination.ImageFile f => PlanToFile(job, f),
            BurnDestination.Drive d => PlanToDrive(shape, job, d),
            _ => throw new BurnNotSupportedException("Unknown destination."),
        };
    }

    private static BurnJobPlan PlanToFile(BurnJob job, BurnDestination.ImageFile file)
    {
        var warnings = new List<string>();

        if (job.Test)
            throw new BurnNotSupportedException(
                "Test is a simulated disc burn and does not apply to an image file destination.");
        if (job.Copies > 1)
            throw new BurnNotSupportedException(
                "Copies applies to discs; an image file destination writes exactly one file.");
        if (job.Method != BurnMethodChoice.Auto)
            warnings.Add("TAO/RAW are disc write strategies and have no effect when writing to a file.");

        var steps = new List<BurnStep>();
        if (job.Write) steps.Add(new BurnStep(BurnStepKind.Write, BurnMethod.Imapi2Data, 1));
        if (job.Verify) steps.Add(new BurnStep(BurnStepKind.Verify, BurnMethod.Imapi2Data, 1));

        return new BurnJobPlan
        {
            Steps = steps,
            DestinationLabel = file.Path,
            IsImageFile = true,
            Warnings = warnings,
        };
    }

    private static BurnJobPlan PlanToDrive(ImageShape shape, BurnJob job, BurnDestination.Drive dest)
    {
        var drive = dest.Capabilities;
        var warnings = new List<string>();

        // Start from what the content + drive actually allow; this throws if the
        // image simply cannot be written by this drive at all.
        var auto = BurnPlanner.Plan(shape, drive);
        warnings.AddRange(auto.Warnings);

        var method = job.Method switch
        {
            BurnMethodChoice.Auto => auto.Method,

            BurnMethodChoice.RawDao96 when !drive.RawDao96 =>
                throw new BurnNotSupportedException(
                    $"RAW was requested but {drive.Vendor} {drive.Model} does not support RAW DAO-96."),
            BurnMethodChoice.RawDao96 => BurnMethod.RawDao96,

            // TAO cannot carry layouts needing raw sector/subchannel control.
            BurnMethodChoice.Tao when auto.Method == BurnMethod.RawDao96 =>
                throw new BurnNotSupportedException(
                    "TAO was requested, but this image (mixed-mode, multisession or audio) " +
                    "needs RAW DAO-96 to be written faithfully."),
            BurnMethodChoice.Tao => BurnMethod.Imapi2Data,

            _ => auto.Method,
        };

        if (job.Method == BurnMethodChoice.Tao && shape.AllAudio && shape.NonStandardGaps)
            warnings.Add("TAO was chosen for audio with non-standard gaps: every gap will become " +
                         "the standard two seconds. RAW DAO is needed to reproduce them exactly.");
        else if (job.Method == BurnMethodChoice.Tao)
            warnings.Add("TAO writes a two-second run-in/run-out between tracks; " +
                         "DAO/SAO is preferred for an exact layout.");
        if (job.Verify && !job.Write)
            warnings.Add("Verify without Write compares the disc already in the drive against the image.");

        var steps = new List<BurnStep>();
        if (job.Test) steps.Add(new BurnStep(BurnStepKind.Test, method, 1));
        for (int copy = 1; copy <= job.Copies; copy++)
        {
            if (job.Write) steps.Add(new BurnStep(BurnStepKind.Write, method, copy));
            if (job.Verify) steps.Add(new BurnStep(BurnStepKind.Verify, method, copy));
        }

        return new BurnJobPlan
        {
            Steps = steps,
            DestinationLabel = $"{drive.Vendor} {drive.Model} ({drive.DevicePath})",
            IsImageFile = false,
            Warnings = warnings,
        };
    }
}
