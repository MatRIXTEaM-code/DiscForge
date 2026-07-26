// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Cue;
using DiscForge.Core.Files;
using DiscForge.Core.Raw;

/// <summary>
/// Commands that work on image files alone — no drive, no platform assumptions.
///
/// This is why dforge stays portable while the GUI does not: everything here
/// runs against bytes on disk, so an archive of images can be checked, browsed
/// and repaired from a script, on any machine, without an optical drive present
/// at all. The drive-bound work — ripping, quality scanning, C2 recovery — needs
/// Windows SPTI and lives in the GUI.
/// </summary>
internal static class ImageCommands
{
    // ---- browse ------------------------------------------------------------

    public static int Browse(string[] args)
    {
        if (args.Length < 2)
            return Fail("usage: dforge browse <image.cdi|image.iso> [--extract <dir>] [--only <pattern>]");
        if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

        string? extractTo = null;
        string? pattern = null;
        for (int i = 2; i < args.Length - 1; i++)
        {
            if (args[i] == "--extract") extractTo = args[i + 1];
            else if (args[i] == "--only") pattern = args[i + 1];
        }

        var listing = ImageBrowser.List(args[1]);
        if (listing.Error is not null) return Fail(listing.Error);

        var files = listing.Files.AsEnumerable();
        if (pattern is not null)
        {
            // Substring rather than glob: simpler to explain, and "--only .jpg"
            // does what people expect without a wildcard tutorial.
            files = files.Where(f => f.Path.Contains(pattern, StringComparison.OrdinalIgnoreCase));
        }
        var chosen = files.ToList();

        Console.WriteLine($"{Path.GetFileName(args[1])}: {listing.Filesystem}" +
                          (listing.VolumeId is not null ? $", volume \"{listing.VolumeId}\"" : ""));
        Console.WriteLine();

        foreach (var f in chosen)
            Console.WriteLine($"  {f.Size,12:N0}  {f.Path}");

        Console.WriteLine();
        Console.WriteLine($"{chosen.Count:N0} file(s), {chosen.Sum(f => f.Size):N0} bytes" +
                          (pattern is not null ? $" (filtered from {listing.Files.Count:N0})" : ""));

        if (extractTo is null) return 0;

        Console.WriteLine();
        Console.WriteLine($"Extracting to {extractTo}…");

        long lastPct = -1;
        var result = ImageBrowser.Extract(args[1], chosen, extractTo, null,
            new Progress<double>(f =>
            {
                long pct = (long)(f * 100);
                if (pct != lastPct && pct % 10 == 0)
                {
                    lastPct = pct;
                    Console.Write($"\r  {pct}%   ");
                }
            }));

        Console.WriteLine($"\r  done      ");
        Console.WriteLine($"Extracted {result.Extracted:N0} file(s), {result.BytesWritten:N0} bytes.");
        foreach (var p in result.Problems) Console.Error.WriteLine("  " + p);
        return result.Failed == 0 ? 0 : 1;
    }

    // ---- cue-check ---------------------------------------------------------

    public static int CueCheck(string[] args)
    {
        if (args.Length < 2)
            return Fail("usage: dforge cue-check <sheet.cue>");
        if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

        CueSheet cue;
        try { cue = CueSheet.Parse(File.ReadAllText(args[1])); }
        catch (Exception ex) { return Fail("Could not parse the sheet: " + ex.Message); }

        var dir = Path.GetDirectoryName(Path.GetFullPath(args[1]))!;
        var result = CueValidator.Validate(cue, dir);

        Console.WriteLine($"{Path.GetFileName(args[1])}: {cue.Tracks.Count} track(s)" +
                          (cue.Title is not null ? $"  \"{cue.Title}\"" : ""));
        Console.WriteLine();

        foreach (var (file, size) in result.FileSizes)
            Console.WriteLine(size < 0
                ? $"  {file}  — NOT FOUND"
                : $"  {file}  {size:N0} bytes");
        Console.WriteLine();

        if (result.Clean)
        {
            Console.WriteLine("OK: every index falls inside the data file, the track types agree,");
            Console.WriteLine("and the arithmetic reaches the end of the file.");
            return 0;
        }

        foreach (var level in new[] { CueIssueLevel.Error, CueIssueLevel.Warning, CueIssueLevel.Info })
        {
            var of = result.Issues.Where(i => i.Level == level).ToList();
            if (of.Count == 0) continue;

            Console.WriteLine(level switch
            {
                CueIssueLevel.Error => "ERRORS — this sheet will not burn or convert correctly:",
                CueIssueLevel.Warning => "Warnings:",
                _ => "Notes:",
            });
            foreach (var i in of) Console.WriteLine("  " + i);
            Console.WriteLine();
        }

        // Exit code carries the verdict so a script can act on it.
        return result.HasErrors ? 2 : result.HasWarnings ? 1 : 0;
    }

    // ---- ecc-repair --------------------------------------------------------

    public static int EccRepair(string[] args)
    {
        if (args.Length < 2)
            return Fail("usage: dforge ecc-repair <image.bin> [--out <repaired.bin>] [--dry-run]\n" +
                        "  Checks every Mode 1 sector's EDC and rebuilds damaged ones from the\n" +
                        "  Reed-Solomon parity the sector already carries. Raw 2352-byte images only.");
        if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

        bool dryRun = args.Contains("--dry-run");
        string? outPath = null;
        for (int i = 2; i < args.Length - 1; i++)
            if (args[i] == "--out") outPath = args[i + 1];

        long length = new FileInfo(args[1]).Length;
        if (length % 2352 != 0)
            return Fail($"'{args[1]}' is {length:N0} bytes — not a whole number of 2352-byte " +
                        "sectors, so it isn't a raw image. ECC repair needs the full sector " +
                        "including its parity.");

        long sectors = length / 2352;
        Console.WriteLine($"{Path.GetFileName(args[1])}: {sectors:N0} sectors");
        Console.WriteLine();

        using var input = File.OpenRead(args[1]);
        FileStream? output = null;
        if (!dryRun)
        {
            outPath ??= Path.ChangeExtension(args[1], ".repaired.bin");
            if (File.Exists(outPath))
                return Fail($"'{outPath}' already exists — refusing to overwrite it.");
            output = File.Create(outPath);
        }

        var sector = new byte[2352];
        long checkedCount = 0, alreadyGood = 0, repaired = 0, unrepairable = 0, skipped = 0;
        var stubborn = new List<long>();

        try
        {
            for (long s = 0; s < sectors; s++)
            {
                input.ReadExactly(sector, 0, 2352);

                if (!IsMode1(sector))
                {
                    // Audio, Mode 2 Form 2, or an empty sector: no Mode 1 parity
                    // to work with, so it passes through untouched.
                    skipped++;
                    output?.Write(sector, 0, 2352);
                    continue;
                }

                checkedCount++;
                var (edcOk, _) = EdcEcc.VerifyMode1(sector);
                if (edcOk)
                {
                    alreadyGood++;
                    output?.Write(sector, 0, 2352);
                    continue;
                }

                // The EDC says this sector is wrong but not where. With no C2
                // pointers there are no erasure positions, so the decoder falls
                // back to finding single errors per codeword — weaker than the
                // erasure case, but it still repairs a great deal.
                var result = EccCorrector.CorrectMode1(sector, Array.Empty<int>());
                if (result.Success)
                {
                    repaired++;
                }
                else
                {
                    unrepairable++;
                    if (stubborn.Count < 40) stubborn.Add(s);
                }

                output?.Write(sector, 0, 2352);

                if ((s & 0x3FFF) == 0)
                    Console.Write($"\r  {100.0 * s / sectors:F0}%   ");
            }
        }
        finally
        {
            output?.Dispose();
        }

        Console.WriteLine("\r        ");
        Console.WriteLine($"  Mode 1 sectors checked : {checkedCount:N0}");
        Console.WriteLine($"  already valid          : {alreadyGood:N0}");
        Console.WriteLine($"  repaired from parity   : {repaired:N0}");
        Console.WriteLine($"  beyond repair          : {unrepairable:N0}");
        Console.WriteLine($"  skipped (not Mode 1)   : {skipped:N0}");
        Console.WriteLine();

        if (stubborn.Count > 0)
        {
            Console.WriteLine("Sectors that could not be repaired:");
            Console.WriteLine("  " + string.Join(", ", stubborn.Select(x => x.ToString("N0"))) +
                              (unrepairable > stubborn.Count ? $", … (+{unrepairable - stubborn.Count:N0})" : ""));
            Console.WriteLine();
        }

        if (dryRun)
        {
            Console.WriteLine("Dry run — nothing was written. Re-run without --dry-run to produce");
            Console.WriteLine("a repaired copy.");
        }
        else if (repaired > 0)
        {
            Console.WriteLine($"Wrote {outPath}");
            Console.WriteLine();
            Console.WriteLine("Each repaired sector was rebuilt from the Reed-Solomon parity stored");
            Console.WriteLine("in the sector itself, and confirmed by its EDC — an independent check");
            Console.WriteLine("computed over the whole sector by a different polynomial. A repair");
            Console.WriteLine("that produced plausible nonsense would fail it.");
        }
        else
        {
            Console.WriteLine($"Wrote {outPath} (an exact copy — nothing needed repairing).");
        }

        return unrepairable == 0 ? 0 : 1;
    }

    /// <summary>True when the sector's own header says Mode 1.</summary>
    private static bool IsMode1(ReadOnlySpan<byte> sector)
    {
        if (sector[0] != 0x00 || sector[11] != 0x00) return false;
        for (int i = 1; i <= 10; i++) if (sector[i] != 0xFF) return false;
        return (sector[15] & 0x03) == 1;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"dforge: {message}");
        return 1;
    }
}