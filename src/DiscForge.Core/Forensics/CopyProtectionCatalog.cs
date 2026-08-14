// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Iso;
using DiscForge.Core.Raw;

namespace DiscForge.Core.Forensics;

/// <summary>How sure we are a scheme is present.</summary>
public enum ProtectionConfidence : byte { Possible = 0, Likely = 1, Confirmed = 2 }

/// <summary>One piece of evidence for a detection — what matched and where.</summary>
public sealed record ProtectionEvidence(string Kind, string Detail);

/// <summary>A detected copy-protection scheme, recorded as preservation metadata.</summary>
public sealed record ProtectionDetection
{
    public required string Scheme { get; init; }
    public string? Version { get; init; }
    public string? Parameters { get; init; }
    public required ProtectionConfidence Confidence { get; init; }
    public required IReadOnlyList<ProtectionEvidence> Evidence { get; init; }
    public required string Note { get; init; }

    public override string ToString()
    {
        string ver = Version is { Length: > 0 } ? $" {Version}" : "";
        string parm = Parameters is { Length: > 0 } ? $" · {Parameters}" : "";
        return $"{Scheme}{ver} [{Confidence}]{parm}";
    }
}

/// <summary>The protection fingerprint of a disc.</summary>
public sealed record ProtectionReport
{
    public required IReadOnlyList<ProtectionDetection> Detections { get; init; }
    public bool AnyFound => Detections.Count > 0;

    public string Summary()
    {
        if (!AnyFound) return "No known copy-protection fingerprints found.";
        var names = Detections.Select(d => d.Version is { Length: > 0 } ? $"{d.Scheme} {d.Version}" : d.Scheme);
        return $"Protection fingerprint: {string.Join(", ", names)}.";
    }
}

/// <summary>
/// Copy-protection fingerprint catalog — precisely identify the protection scheme, version and
/// parameters a disc carries and record them as preservation <i>metadata</i>. It matches the marks a
/// scheme leaves behind: the tell-tale files and directories it drops on the filesystem (SafeDisc's
/// <c>00000001.TMP</c> and <c>.icd</c>, SecuROM's <c>CMS*.DLL</c>, a <c>LASERLOK</c> directory…),
/// signature strings inside the wrapped executables (and SafeDisc's exact version triplet), and — for
/// PlayStation discs — the deliberately corrupted subchannel Q of LibCrypt, caught by its failing CRC.
/// This is detection and documentation only: it names and dates what is there so a faithful dump can be
/// catalogued the way a museum labels an artefact. It never removes, bypasses, weakens or circumvents
/// any protection, and knowing a disc is SafeDisc-protected does not help you copy it.
/// </summary>
public static class CopyProtectionCatalog
{
    private enum MarkKind { File, Directory, Extension, ExeString }

    private sealed record Mark(string Value, MarkKind Kind, bool Strong);

    private sealed record Signature(string Scheme, IReadOnlyList<Mark> Marks, string? VersionKey = null);

    // Well-known schemes and the distinctive marks they leave. Marks flagged Strong are, on their
    // own, enough to confirm; supporting marks only raise a "likely".
    private static readonly IReadOnlyList<Signature> Catalog = new List<Signature>
    {
        new("SafeDisc", new[]
        {
            new Mark("00000001.TMP", MarkKind.File, true),
            new Mark("00000002.TMP", MarkKind.File, false),
            new Mark("DPLAYERX.DLL", MarkKind.File, true),
            new Mark("drvmgt.dll", MarkKind.File, false),
            new Mark("secdrv.sys", MarkKind.File, false),
            new Mark("CLCD16.DLL", MarkKind.File, true),
            new Mark("CLCD32.DLL", MarkKind.File, true),
            new Mark("CLOKSPL.EXE", MarkKind.File, true),
            new Mark(".icd", MarkKind.Extension, true),
            new Mark("BoG_ *90.0&!!  Yy>", MarkKind.ExeString, true),
            new Mark("stxt371", MarkKind.ExeString, true),
            new Mark("stxt774", MarkKind.ExeString, true),
        }, VersionKey: "SafeDisc"),

        new("SecuROM", new[]
        {
            new Mark("CMS16.DLL", MarkKind.File, true),
            new Mark("CMS_95.DLL", MarkKind.File, true),
            new Mark("CMS_NT.DLL", MarkKind.File, true),
            new Mark("sintf32.dll", MarkKind.File, false),
            new Mark("sintfnt.dll", MarkKind.File, false),
            new Mark(".securom", MarkKind.ExeString, true),
            new Mark("AddD", MarkKind.ExeString, false),
            new Mark(".cms_t", MarkKind.ExeString, true),
            new Mark(".cms_d", MarkKind.ExeString, true),
        }, VersionKey: "SecuROM"),

        new("LaserLock", new[]
        {
            new Mark("LASERLOK", MarkKind.Directory, true),
            new Mark("NOMOUSE.SP", MarkKind.File, true),
            new Mark("NOMOUSE.COM", MarkKind.File, true),
            new Mark("LASERLOK.IN", MarkKind.File, true),
            new Mark("Packed by LASERLOK", MarkKind.ExeString, true),
        }),

        new("CD-Cops", new[]
        {
            new Mark("CDCOPS.DLL", MarkKind.File, true),
            new Mark(".GZ_", MarkKind.Extension, true),
            new Mark(".W_X", MarkKind.Extension, true),
            new Mark(".Qz", MarkKind.Extension, true),
            new Mark("CD-Cops,  ver.", MarkKind.ExeString, true),
        }, VersionKey: "CD-Cops"),

        new("StarForce", new[]
        {
            new Mark("protect.dll", MarkKind.File, false),
            new Mark("protect.exe", MarkKind.File, false),
            new Mark("sfdrv01.sys", MarkKind.File, true),
            new Mark("sfsync02.sys", MarkKind.File, true),
            new Mark("sfsync04.sys", MarkKind.File, true),
            new Mark("Protection Technology", MarkKind.ExeString, false),
            new Mark(".sforce", MarkKind.ExeString, true),
        }),

        new("VOB ProtectCD", new[]
        {
            new Mark("VOB ProtectCD", MarkKind.ExeString, true),
            new Mark(".grand", MarkKind.ExeString, true),
        }),

        new("TAGES", new[]
        {
            new Mark("tages.dll", MarkKind.File, true),
            new Mark("tagesclient.exe", MarkKind.File, true),
            new Mark("devprot.sys", MarkKind.File, false),
            new Mark("protected-tages", MarkKind.ExeString, true),
        }),
    };

    /// <summary>A binary whose bytes should be scanned for executable signatures.</summary>
    public readonly record struct ScannedBinary(string Name, byte[] Data);

    /// <summary>
    /// Fingerprint a disc from its file list, optionally the bytes of executables to scan for
    /// in-file signatures, and optionally the disc's subchannel Q frames (for LibCrypt).
    /// </summary>
    public static ProtectionReport Identify(
        IEnumerable<string> filePaths,
        IReadOnlyList<ScannedBinary>? binaries = null,
        IReadOnlyList<byte[]>? subchannelQ = null)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        // Index the filesystem: basenames, directory segments, extensions.
        var basenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var withExt = new List<string>();
        foreach (var raw in filePaths)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            string p = raw.Replace('\\', '/');
            var segs = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 1 < segs.Length; i++) dirs.Add(StripVersion(segs[i]));
            if (segs.Length == 0) continue;
            string name = StripVersion(segs[^1]);
            basenames.Add(name);
            withExt.Add(name);
        }

        var detections = new List<ProtectionDetection>();

        foreach (var sig in Catalog)
        {
            var evidence = new List<ProtectionEvidence>();
            bool strong = false, supporting = false;

            foreach (var m in sig.Marks)
            {
                bool hit = m.Kind switch
                {
                    MarkKind.File => basenames.Contains(m.Value),
                    MarkKind.Directory => dirs.Contains(m.Value),
                    MarkKind.Extension => withExt.Any(n => n.EndsWith(m.Value, StringComparison.OrdinalIgnoreCase)),
                    MarkKind.ExeString => binaries is not null && binaries.Any(b => ContainsAscii(b.Data, m.Value)),
                    _ => false,
                };
                if (!hit) continue;

                if (m.Strong) strong = true; else supporting = true;
                evidence.Add(new ProtectionEvidence(
                    m.Kind switch
                    {
                        MarkKind.File => "file",
                        MarkKind.Directory => "directory",
                        MarkKind.Extension => "extension",
                        _ => "exe-signature",
                    },
                    m.Value));
            }

            if (evidence.Count == 0) continue;

            var confidence = strong ? ProtectionConfidence.Confirmed
                           : supporting ? ProtectionConfidence.Likely
                           : ProtectionConfidence.Possible;

            string? version = sig.VersionKey is null ? null : ExtractVersion(sig.VersionKey, binaries);

            detections.Add(new ProtectionDetection
            {
                Scheme = sig.Scheme,
                Version = version,
                Confidence = confidence,
                Evidence = evidence,
                Note = "Preservation metadata only — DiscForge records the scheme; it does not bypass it.",
            });
        }

        if (subchannelQ is { Count: > 0 })
        {
            var lc = DetectLibCrypt(subchannelQ);
            if (lc is not null) detections.Add(lc);
        }

        return new ProtectionReport { Detections = detections };
    }

    /// <summary>Fingerprint straight from a cooked ISO 9660 image: enumerate its files, scan the
    /// executables (bounded in size) for signatures, and match the catalog.</summary>
    public static ProtectionReport FromIso(byte[] isoImage, IReadOnlyList<byte[]>? subchannelQ = null,
                                           int maxExeBytes = 4 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(isoImage);
        var paths = new List<string>();
        var binaries = new List<ScannedBinary>();

        try
        {
            IsoDirectory dir;
            using (var ms = new MemoryStream(isoImage, writable: false))
                dir = IsoReader.Read(ms);

            foreach (var f in dir.Files)
            {
                paths.Add(f.Path);
                string name = StripVersion(f.Path);
                if (!IsScannable(name)) continue;
                long start = (long)f.Extent * 2048;
                long len = Math.Min(f.Size, maxExeBytes);
                if (start < 0 || start + len > isoImage.Length || len <= 0) continue;
                binaries.Add(new ScannedBinary(name, isoImage.AsSpan((int)start, (int)len).ToArray()));
            }
        }
        catch
        {
            // Not a readable ISO — fall back to whatever (nothing) we gathered.
        }

        return Identify(paths, binaries, subchannelQ);
    }

    /// <summary>Detect LibCrypt: PlayStation subchannel protection that stores deliberately wrong Q
    /// frames. Each frame's CRC-16 is checked; frames that fail are the corrupted ones. Returns a
    /// detection when any are present, or null otherwise.</summary>
    public static ProtectionDetection? DetectLibCrypt(IReadOnlyList<byte[]> qFrames)
    {
        ArgumentNullException.ThrowIfNull(qFrames);
        int checkedFrames = 0, bad = 0;
        foreach (var q in qFrames)
        {
            if (q is null || q.Length < 12) continue;
            checkedFrames++;
            if (!RawSubchannel.QCrcValid(q)) bad++;
        }
        if (bad == 0) return null;

        // LibCrypt corrupts a modest number of frames (classically in pairs); a couple is enough
        // to fingerprint, a lone one is only suggestive.
        var confidence = bad >= 2 ? ProtectionConfidence.Confirmed : ProtectionConfidence.Likely;
        return new ProtectionDetection
        {
            Scheme = "LibCrypt",
            Confidence = confidence,
            Parameters = $"{bad} corrupted subchannel Q frame(s) of {checkedFrames} checked",
            Evidence = new[] { new ProtectionEvidence("subchannel", $"{bad} Q frame(s) fail CRC-16") },
            Note = "Subchannel corruption consistent with LibCrypt; preserve the subchannel verbatim. " +
                   "Metadata only — DiscForge documents the pattern, it does not defeat it. Confirm against a second dump.",
        };
    }

    public static string Render(ProtectionReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        foreach (var d in r.Detections)
        {
            sb.AppendLine($"  {d}");
            foreach (var e in d.Evidence) sb.AppendLine($"      - {e.Kind}: {e.Detail}");
            sb.AppendLine($"      note: {d.Note}");
        }
        return sb.ToString().TrimEnd();
    }

    // ---- internals ----------------------------------------------------------

    // SafeDisc stores an exact version triplet after a fixed signature; CD-Cops trails a version
    // string after its marker. Others carry no cleanly-parseable version here.
    private static string? ExtractVersion(string key, IReadOnlyList<ScannedBinary>? binaries)
    {
        if (binaries is null) return null;
        switch (key)
        {
            case "SafeDisc":
            {
                var sig = Encoding.ASCII.GetBytes("BoG_ *90.0&!!  Yy>");
                foreach (var b in binaries)
                {
                    int i = IndexOf(b.Data, sig);
                    if (i < 0) continue;
                    int p = i + 20;                        // three little-endian int32 follow
                    if (p + 12 > b.Data.Length) return null;
                    int v1 = ReadI32(b.Data, p), v2 = ReadI32(b.Data, p + 4), v3 = ReadI32(b.Data, p + 8);
                    if (v1 is < 0 or > 20) return null;     // sanity — SafeDisc majors are small
                    return $"{v1}.{v2:00}.{v3:0000}";
                }
                return null;
            }
            case "CD-Cops":
            {
                var sig = Encoding.ASCII.GetBytes("CD-Cops,  ver.");
                foreach (var b in binaries)
                {
                    int i = IndexOf(b.Data, sig);
                    if (i < 0) continue;
                    int p = i + sig.Length;
                    var v = new StringBuilder();
                    while (p < b.Data.Length && v.Length < 8)
                    {
                        char c = (char)b.Data[p++];
                        if (c is (>= '0' and <= '9') or '.' or ' ') { if (c != ' ' || v.Length > 0) v.Append(c); }
                        else break;
                    }
                    string s = v.ToString().Trim();
                    return s.Length > 0 ? s : null;
                }
                return null;
            }
            case "SecuROM":
            {
                // Coarse era from the section marks, not a precise version.
                if (binaries.Any(b => ContainsAscii(b.Data, ".securom"))) return "(v7/v8-era)";
                return null;
            }
        }
        return null;
    }

    private static bool IsScannable(string name)
    {
        foreach (var ext in new[] { ".exe", ".dll", ".sys", ".icd", ".ovl", ".vxd", ".386" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string StripVersion(string segment)
    {
        int semi = segment.IndexOf(';');
        string s = semi >= 0 ? segment[..semi] : segment;
        return s.Trim();
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

    private static int ReadI32(byte[] b, int p)
        => b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24);
}
