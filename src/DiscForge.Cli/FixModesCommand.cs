// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Cdi;

/// <summary>
/// `dforge fix-modes` — corrects the track mode recorded in a CDI descriptor
/// when it disagrees with what the stored sectors actually are.
///
/// The case this exists for: DiscForge wrote images whose descriptor said Mode 1
/// for tracks that are really Mode 2, because the mode wasn't probed from the
/// disc. Every consumer trusts that field — user data starts at offset 16 for
/// Mode 1 and 24 for Mode 2 — so such an image reads eight bytes early on every
/// sector: no filesystem, useless extraction, and nothing saying why. Re-reading
/// the disc fixes it, but the disc isn't always still to hand, and the image
/// itself contains all the evidence needed.
/// </summary>
internal static class FixModesCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
            return Fail("usage: dforge fix-modes <image.cdi> [--dry-run]");

        string path = args[1];
        if (!File.Exists(path))
            return Fail($"File not found: {path}");

        bool dryRun = args.Contains("--dry-run");

        // Analysis opens read-only: inspecting an image must never be able to
        // damage it, whatever else goes wrong.
        CdiModeRepairReport report;
        try
        {
            using var ro = File.OpenRead(path);
            report = CdiModeRepair.Analyse(ro);
        }
        catch (Exception ex) { return Fail(ex.Message); }

        Console.WriteLine($"File: {Path.GetFileName(path)}");
        Console.WriteLine();
        Console.WriteLine("   #  Declared  Actual    Status");
        Console.WriteLine("  --  --------  --------  ---------------------------------------------");
        foreach (var f in report.Findings)
        {
            string actual = f.Actual?.ToString() ?? "-";
            string status = f.NeedsRepair ? "MISMATCH - will be corrected" : f.Detail;
            Console.WriteLine($"  {f.TrackNumber,2}  {f.Declared,-8}  {actual,-8}  {status}");
        }
        Console.WriteLine();

        foreach (var n in report.Notes)
            Console.WriteLine($"note: {n}");

        if (!report.AnyRepairNeeded)
        {
            Console.WriteLine("Every track's declared mode matches its sectors. Nothing to do.");
            return 0;
        }

        if (dryRun)
        {
            Console.WriteLine($"{report.RepairsNeeded} track(s) would be corrected. " +
                              "Re-run without --dry-run to apply.");
            return 0;
        }

        if (!report.DescriptorLayoutVerified)
            return Fail("The descriptor layout could not be verified, so nothing was changed. " +
                        "Re-read the disc instead.");

        int patched;
        try
        {
            using var rw = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
            patched = CdiModeRepair.Repair(rw, out _);
        }
        catch (Exception ex) { return Fail(ex.Message); }

        Console.WriteLine($"Corrected {patched} track mode(s). Only the descriptor's mode fields " +
                          "were changed; no track data was touched.");
        Console.WriteLine("Run 'dforge ls' on the image to confirm the filesystem now reads.");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"dforge: {message}");
        return 1;
    }
}