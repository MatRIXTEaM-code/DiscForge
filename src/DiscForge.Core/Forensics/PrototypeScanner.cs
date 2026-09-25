// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Security.Cryptography;
using System.Text;
using DiscForge.Core.Iso;

namespace DiscForge.Core.Forensics;

/// <summary>What kind of prototype/debug-build residue a finding is.</summary>
public enum ResidueKind : byte
{
    /// <summary>A filename or directory whose extension/path is a classic debug-build leftover
    /// (.sym, .map, .pdb, a /debug/ or /devkit/ directory…).</summary>
    DebugFile,
    /// <summary>A debug/diagnostic string found inside a scanned executable (an assert message,
    /// a build-machine path, a PDB reference).</summary>
    DebugString,
}

/// <summary>One piece of leftover debug/prototype-build residue.</summary>
public sealed record ResidueFinding
{
    public required ResidueKind Kind { get; init; }
    /// <summary>What matched: the file path, or the string found in a binary.</summary>
    public required string Detail { get; init; }
    /// <summary>Which file this was found in — the file itself for <see cref="ResidueKind.DebugFile"/>,
    /// or the scanned binary's name for <see cref="ResidueKind.DebugString"/>.</summary>
    public required string InFile { get; init; }
}

/// <summary>One line of an entry's own bill-of-materials in a retail-baseline comparison.</summary>
public sealed record BaselineEntry(string Path, long Size, string? Sha256 = null);

public enum BaselineDiffKind : byte { AddedHere, MissingHere, SizeMismatch, HashMismatch }

/// <summary>One difference between this disc and a known-retail file manifest.</summary>
public sealed record BaselineDiffEntry
{
    public required BaselineDiffKind Kind { get; init; }
    public required string Path { get; init; }
    public required string Detail { get; init; }
}

/// <summary>The result of a prototype/debug-residue sweep over one disc.</summary>
public sealed record PrototypeReport
{
    public required string VolumeId { get; init; }
    public required string? BuildDate { get; init; }
    public required int FileCount { get; init; }
    public required IReadOnlyList<ResidueFinding> Residue { get; init; }
    public required IReadOnlyList<BaselineDiffEntry> BaselineDiff { get; init; }

    /// <summary>Any residue at all, or a baseline diff that isn't just a size wobble, is enough
    /// to call this "worth a second look" — never a certainty, since a retail disc can carry
    /// leftover debug files by mastering accident too. This flags for review, it doesn't rule.</summary>
    public bool LooksLikePrototype => Residue.Count > 0 ||
        BaselineDiff.Any(d => d.Kind is BaselineDiffKind.AddedHere or BaselineDiffKind.MissingHere);

    public string Summary()
    {
        string built = BuildDate is { Length: > 0 } ? $", mastered {BuildDate}" : "";
        if (!LooksLikePrototype)
            return $"{VolumeId}{built}: no debug/prototype residue found" +
                   (BaselineDiff.Count > 0 ? $" ({BaselineDiff.Count} minor baseline difference(s))." : ".");
        return $"{VolumeId}{built}: {Residue.Count} residue finding(s), " +
               $"{BaselineDiff.Count} baseline difference(s) — worth a closer look, not a confirmed prototype.";
    }
}

/// <summary>
/// Prototype / debug-residue scanner: a dedicated "is this a prototype, not the retail disc?"
/// pass. Three independent signals, each real evidence on its own and stronger together:
///
/// 1. Leftover debug files — symbol tables (.sym/.map/.pdb), directories named debug/devkit/qa,
///    all classic residue from a build that wasn't stripped for retail.
/// 2. Debug strings inside executables — assert messages, a build machine's own file paths
///    (a developer's `E:\p4\...` or `\\devkit\...`), or an embedded PDB reference (the PE
///    CodeView debug-directory signature `RSDS` followed by a `.pdb` path) — a compiler leaves
///    these in an unstripped/debug build and a retail master strips them out.
/// 3. A diff against a known-retail file manifest, when one is supplied — files present here
///    that retail doesn't have (or vice versa), or size/hash mismatches on files retail does.
///
/// Builds on <see cref="DiscBillOfMaterials"/>'s mastering-date extraction (<see cref="DiscChronology"/>)
/// and follows <see cref="DiscArchaeology"/>'s house rule: detection and documentation only,
/// never a verdict. A clean disc can still BE a prototype (nothing left behind); a disc with
/// hits here is not proven one (debug files occasionally ship by mastering accident). This
/// flags what's worth a human's attention, nothing more.
/// </summary>
public static class PrototypeScanner
{
    private static readonly string[] DebugExtensions =
        { ".sym", ".map", ".pdb", ".cvd", ".ndb", ".dbg", ".elf.map" };

    private static readonly string[] DevPathFragments =
        { "debug", "devkit", "dev-kit", "internal", "proto", "prototype", "qa", "_build", "sandbox" };

    // ASCII needles a debug/unstripped build tends to leave in its executables. Deliberately
    // generic (not tied to one engine or platform) — anything more specific belongs in
    // DiscBillOfMaterials' engine catalog, not here.
    private static readonly string[] DebugStringNeedles =
        { "ASSERTION FAILED", "Assertion failed", "DEBUG BUILD", "__FILE__", ":\\perforce\\", ":\\p4\\",
          "\\devkit\\", "\\dev\\", "jenkins", "teamcity", "buildbot" };

    /// <summary>Filenames/directories whose extension or path segment is classic debug-build
    /// residue — a symbol table, a directory literally called "debug" or "devkit".</summary>
    public static IReadOnlyList<ResidueFinding> ScanFileNames(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        var found = new List<ResidueFinding>();
        foreach (var raw in filePaths)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            string p = raw.Replace('\\', '/');
            // ISO 9660 basenames carry a ";N" version suffix (e.g. "GAME.SYM;1") — strip it
            // before matching the extension, or every extension check misses.
            string strippedLower = StripVersion(p).ToLowerInvariant();

            foreach (var ext in DebugExtensions)
                if (strippedLower.EndsWith(ext, StringComparison.Ordinal))
                {
                    found.Add(new ResidueFinding { Kind = ResidueKind.DebugFile, Detail = p, InFile = p });
                    break;
                }

            var segs = strippedLower.Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (var seg in segs)
                if (DevPathFragments.Any(f => seg == f || seg.Contains(f)))
                {
                    found.Add(new ResidueFinding { Kind = ResidueKind.DebugFile, Detail = p, InFile = p });
                    break;
                }
        }
        return found;
    }

    /// <summary>Debug/diagnostic strings inside scanned executables, including an embedded PDB
    /// reference: a debug build's linker writes a CodeView debug-directory entry whose data
    /// starts with the ASCII signature "RSDS" and is followed by the absolute path to the
    /// generated .pdb — this is a heuristic byte scan for that pattern, not a full PE parse,
    /// so it can miss a compressed/packed executable, but it never false-positives on the
    /// magic + a real ".pdb" path within a short span of it.</summary>
    public static IReadOnlyList<ResidueFinding> ScanBinaries(IReadOnlyList<CopyProtectionCatalog.ScannedBinary> binaries)
    {
        ArgumentNullException.ThrowIfNull(binaries);
        var found = new List<ResidueFinding>();
        foreach (var b in binaries)
        {
            foreach (var needle in DebugStringNeedles)
                if (ContainsAscii(b.Data, needle))
                    found.Add(new ResidueFinding { Kind = ResidueKind.DebugString, Detail = needle, InFile = b.Name });

            int rsds = IndexOf(b.Data, Encoding.ASCII.GetBytes("RSDS"));
            if (rsds >= 0)
            {
                string pdbPath = ExtractPdbPathNear(b.Data, rsds);
                if (pdbPath is { Length: > 0 })
                    found.Add(new ResidueFinding
                    {
                        Kind = ResidueKind.DebugString,
                        Detail = $"embedded PDB reference: {pdbPath}",
                        InFile = b.Name,
                    });
            }
        }
        return found;
    }

    /// <summary>Compare this disc's files against a known-retail manifest: what's here that
    /// retail doesn't have, what retail has that isn't here, and size/hash mismatches on files
    /// both sides carry. Path matching is case-insensitive and slash-normalised; hash is only
    /// compared when both sides supply one.</summary>
    public static IReadOnlyList<BaselineDiffEntry> DiffAgainstRetailBaseline(
        IEnumerable<(string Path, long Size, string? Sha256)> here, IReadOnlyList<BaselineEntry> baseline)
    {
        ArgumentNullException.ThrowIfNull(here);
        ArgumentNullException.ThrowIfNull(baseline);

        string Norm(string p) => p.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

        var hereMap = new Dictionary<string, (long Size, string? Sha256)>();
        foreach (var (path, size, sha) in here) hereMap[Norm(path)] = (size, sha);
        var baseMap = baseline.ToDictionary(e => Norm(e.Path), e => e);

        var diffs = new List<BaselineDiffEntry>();
        foreach (var (key, v) in hereMap)
        {
            if (!baseMap.TryGetValue(key, out var b))
            {
                diffs.Add(new BaselineDiffEntry { Kind = BaselineDiffKind.AddedHere, Path = key,
                    Detail = $"{v.Size:N0} byte(s) — not in the retail baseline" });
                continue;
            }
            if (v.Size != b.Size)
                diffs.Add(new BaselineDiffEntry { Kind = BaselineDiffKind.SizeMismatch, Path = key,
                    Detail = $"here {v.Size:N0} byte(s), retail {b.Size:N0} byte(s)" });
            else if (v.Sha256 is { Length: > 0 } && b.Sha256 is { Length: > 0 } &&
                     !string.Equals(v.Sha256, b.Sha256, StringComparison.OrdinalIgnoreCase))
                diffs.Add(new BaselineDiffEntry { Kind = BaselineDiffKind.HashMismatch, Path = key,
                    Detail = "same size, different content (SHA-256 mismatch)" });
        }
        foreach (var key in baseMap.Keys)
            if (!hereMap.ContainsKey(key))
                diffs.Add(new BaselineDiffEntry { Kind = BaselineDiffKind.MissingHere, Path = key,
                    Detail = $"in the retail baseline but not on this disc ({baseMap[key].Size:N0} byte(s))" });

        return diffs.OrderBy(d => d.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>Build a retail baseline from a known-good image: every file's path, size and
    /// SHA-256 — save it once, diff every suspect dump against it forever after.</summary>
    public static IReadOnlyList<BaselineEntry> BuildBaseline(byte[] isoImage)
    {
        ArgumentNullException.ThrowIfNull(isoImage);
        var entries = new List<BaselineEntry>();
        using var ms = new MemoryStream(isoImage, writable: false);
        var dir = IsoReader.Read(ms);
        using var sha = SHA256.Create();
        foreach (var f in dir.Files)
        {
            long start = (long)f.Extent * 2048;
            long len = f.Size;
            if (start < 0 || len < 0 || start + len > isoImage.Length) continue;
            byte[] hash = sha.ComputeHash(isoImage, (int)start, (int)len);
            entries.Add(new BaselineEntry(f.Path, len, System.Convert.ToHexString(hash).ToLowerInvariant()));
        }
        return entries;
    }

    public static PrototypeReport Analyze(
        string volumeId, IEnumerable<string> filePaths,
        IReadOnlyList<CopyProtectionCatalog.ScannedBinary> binaries,
        IEnumerable<(string Path, long Size, string? Sha256)>? filesWithSize,
        IReadOnlyList<BaselineEntry>? baseline, MasteringReport? chronology)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        var pathList = filePaths as IReadOnlyList<string> ?? filePaths.ToList();

        var residue = new List<ResidueFinding>();
        residue.AddRange(ScanFileNames(pathList));
        residue.AddRange(ScanBinaries(binaries));

        var baselineDiff = baseline is { Count: > 0 } && filesWithSize is not null
            ? DiffAgainstRetailBaseline(filesWithSize, baseline)
            : Array.Empty<BaselineDiffEntry>();

        string? buildDate = chronology?.VolumeCreated is { IsValid: true } vc ? vc.ToString() : null;

        return new PrototypeReport
        {
            VolumeId = volumeId,
            BuildDate = buildDate,
            FileCount = pathList.Count,
            Residue = residue,
            BaselineDiff = baselineDiff,
        };
    }

    /// <summary>Fingerprint straight from a cooked ISO 9660 image, mirroring
    /// <see cref="CopyProtectionCatalog.FromIso"/>/<see cref="DiscBillOfMaterials.FromIso"/>'s
    /// pattern: enumerate files, scan scannable binaries, fold in the disc's own mastering
    /// date, and optionally diff against a supplied retail baseline.</summary>
    public static PrototypeReport FromIso(byte[] isoImage, IReadOnlyList<BaselineEntry>? baseline = null,
                                          int maxExeBytes = 4 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(isoImage);
        var paths = new List<string>();
        var withSize = new List<(string, long, string?)>();
        var binaries = new List<CopyProtectionCatalog.ScannedBinary>();
        string volumeId = "";

        try
        {
            IsoDirectory dir;
            using (var ms = new MemoryStream(isoImage, writable: false))
                dir = IsoReader.Read(ms);
            volumeId = dir.VolumeId ?? "";

            using var sha = baseline is { Count: > 0 } ? SHA256.Create() : null;
            foreach (var f in dir.Files)
            {
                paths.Add(f.Path);
                long start = (long)f.Extent * 2048;
                long len = f.Size;
                bool inBounds = start >= 0 && len >= 0 && start + len <= isoImage.Length;

                string? hash = null;
                if (sha is not null && inBounds)
                    hash = System.Convert.ToHexString(sha.ComputeHash(isoImage, (int)start, (int)len)).ToLowerInvariant();
                withSize.Add((f.Path, len, hash));

                string name = StripVersion(f.Path);
                if (!IsScannable(name) || !inBounds) continue;
                long scanLen = Math.Min(len, maxExeBytes);
                if (scanLen <= 0) continue;
                binaries.Add(new CopyProtectionCatalog.ScannedBinary(name, isoImage.AsSpan((int)start, (int)scanLen).ToArray()));
            }
        }
        catch { /* not a readable ISO — residue/baseline diff only from whatever we gathered */ }

        MasteringReport? chronology = null;
        try { chronology = DiscChronology.Analyze(isoImage); } catch { }

        return Analyze(volumeId, paths, binaries, withSize, baseline, chronology);
    }

    public static string Render(PrototypeReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        foreach (var f in r.Residue)
            sb.AppendLine($"  [{f.Kind}] {f.Detail}  (in {f.InFile})");
        foreach (var d in r.BaselineDiff)
            sb.AppendLine($"  [{d.Kind}] {d.Path}: {d.Detail}");
        return sb.ToString().TrimEnd();
    }

    // ---- internals ------------------------------------------------------------------------

    private static bool IsScannable(string name)
    {
        foreach (var ext in new[] { ".exe", ".dll", ".sys", ".icd", ".ovl", ".vxd", ".386", ".elf", ".bin", ".prx" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string StripVersion(string segment)
    {
        int semi = segment.IndexOf(';');
        return semi >= 0 ? segment[..semi].Trim() : segment.Trim();
    }

    private static bool ContainsAscii(byte[] haystack, string needle)
        => IndexOf(haystack, Encoding.ASCII.GetBytes(needle)) >= 0;

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
        int last = haystack.Length - needle.Length;
        for (int i = 0; i <= last; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    /// <summary>Look for a plausible ".pdb" path within a short span after an "RSDS" magic —
    /// the CodeView record's fixed fields (a 16-byte GUID + a 4-byte age) sit between the magic
    /// and the path, so scan a generous window rather than the exact offset.</summary>
    private static string ExtractPdbPathNear(byte[] data, int rsdsIndex)
    {
        int start = rsdsIndex + 4;
        int windowEnd = Math.Min(data.Length, start + 512);
        var sb = new StringBuilder();
        for (int i = start; i < windowEnd; i++)
        {
            byte b = data[i];
            if (b is >= 32 and < 127)
            {
                sb.Append((char)b);
                if (sb.Length > 260) break;   // MAX_PATH-ish sanity cap
            }
            else if (sb.Length > 0)
            {
                string candidate = sb.ToString();
                if (candidate.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) && candidate.Length > 4)
                    return candidate;
                sb.Clear();
            }
        }
        if (sb.Length > 0)
        {
            string candidate = sb.ToString();
            if (candidate.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) return candidate;
        }
        return "";
    }
}
