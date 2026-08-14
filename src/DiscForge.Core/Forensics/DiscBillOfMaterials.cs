// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Iso;

namespace DiscForge.Core.Forensics;

/// <summary>What kind of technology a detected component is.</summary>
public enum BomCategory : byte { Engine, Audio, Video, Physics, Runtime, AssetPipeline, Platform }

/// <summary>One identified technology on a disc — an engine, a middleware, a compiler runtime,
/// an asset pipeline — recorded as part of the disc's technical bill-of-materials.</summary>
public sealed record TechComponent(
    BomCategory Category, string Name, string? Version,
    ProtectionConfidence Confidence, IReadOnlyList<ProtectionEvidence> Evidence)
{
    public override string ToString()
    {
        string v = Version is { Length: > 0 } ? $" {Version}" : "";
        return $"{Category}: {Name}{v} [{Confidence}]";
    }
}

/// <summary>A disc's technical dossier — what it was built with and when.</summary>
public sealed record DiscBom
{
    public required string VolumeId { get; init; }
    public required string? BuildDate { get; init; }
    public required string? EarliestFile { get; init; }
    public required string? LatestFile { get; init; }
    public required int FileCount { get; init; }
    public required IReadOnlyList<TechComponent> Components { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    public string Summary()
    {
        string built = BuildDate is { Length: > 0 } ? $", mastered {BuildDate}" : "";
        return Components.Count == 0
            ? $"{VolumeId}{built}: no recognised engine/middleware signatures."
            : $"{VolumeId}{built}: {Components.Count} technology component(s) identified.";
    }
}

/// <summary>
/// Software bill-of-materials — a technical dossier for a disc, the way a modern build has an SBOM.
/// It reads what the disc was actually built with: the game engine (Unreal, Unity, RenderWare…), the
/// middleware (Bink and Smacker video, Miles/FMOD audio, Havok/PhysX physics), the compiler runtime it
/// was linked against, the platform's asset pipeline (a PlayStation disc's STR/XA/VAG/TIM files give it
/// away), and — folded in from the disc's own timestamps — when it was mastered. Each finding carries
/// its evidence and a confidence. Invaluable for researchers, emulator authors and cataloguers, and
/// nobody really builds it. Detection and documentation only; it reads what is there and reports.
/// </summary>
public static class DiscBillOfMaterials
{
    private enum MarkKind { File, Directory, Extension, ExeString }
    private sealed record Mark(string Value, MarkKind Kind, bool Strong);
    private sealed record Component(BomCategory Category, string Name, IReadOnlyList<Mark> Marks);

    private static readonly IReadOnlyList<Component> Catalog = new List<Component>
    {
        // ---- Engines --------------------------------------------------------
        new(BomCategory.Engine, "Unreal Engine", new[]
        {
            new Mark(".upk", MarkKind.Extension, true), new Mark(".u", MarkKind.Extension, false),
            new Mark("UnrealEd", MarkKind.ExeString, true), new Mark("Unreal Engine", MarkKind.ExeString, true),
        }),
        new(BomCategory.Engine, "Unity", new[]
        {
            new Mark("UnityPlayer.dll", MarkKind.File, true), new Mark("globalgamemanagers", MarkKind.File, true),
            new Mark(".assets", MarkKind.Extension, false), new Mark("UnityEngine", MarkKind.ExeString, true),
        }),
        new(BomCategory.Engine, "RenderWare", new[]
        {
            new Mark(".dff", MarkKind.Extension, true), new Mark(".txd", MarkKind.Extension, true),
            new Mark(".rws", MarkKind.Extension, true), new Mark("RenderWare", MarkKind.ExeString, true),
        }),
        new(BomCategory.Engine, "id Tech", new[]
        {
            new Mark(".bsp", MarkKind.Extension, false), new Mark(".pak", MarkKind.Extension, false),
            new Mark("id Tech", MarkKind.ExeString, true),
        }),

        // ---- Audio middleware ----------------------------------------------
        new(BomCategory.Audio, "Miles Sound System", new[]
        {
            new Mark("mss32.dll", MarkKind.File, true), new Mark("mss16.dll", MarkKind.File, true),
            new Mark("Miles Sound System", MarkKind.ExeString, true),
        }),
        new(BomCategory.Audio, "FMOD", new[]
        {
            new Mark("fmod.dll", MarkKind.File, true), new Mark("fmodex.dll", MarkKind.File, true),
            new Mark("FMOD", MarkKind.ExeString, false),
        }),
        new(BomCategory.Audio, "Ogg Vorbis", new[]
        {
            new Mark(".ogg", MarkKind.Extension, true), new Mark("Xiph.Org", MarkKind.ExeString, true),
            new Mark("vorbis", MarkKind.ExeString, false),
        }),

        // ---- Video middleware ----------------------------------------------
        new(BomCategory.Video, "Bink Video (RAD)", new[]
        {
            new Mark(".bik", MarkKind.Extension, true), new Mark("binkw32.dll", MarkKind.File, true),
            new Mark("Bink", MarkKind.ExeString, false),
        }),
        new(BomCategory.Video, "Smacker (RAD)", new[]
        {
            new Mark(".smk", MarkKind.Extension, true), new Mark("Smacker", MarkKind.ExeString, true),
        }),

        // ---- Physics --------------------------------------------------------
        new(BomCategory.Physics, "Havok", new[]
        {
            new Mark("Havok", MarkKind.ExeString, true), new Mark("hkClass", MarkKind.ExeString, false),
        }),
        new(BomCategory.Physics, "NVIDIA PhysX / NovodeX", new[]
        {
            new Mark("PhysXLoader.dll", MarkKind.File, true), new Mark("NovodeX", MarkKind.ExeString, true),
            new Mark("PhysX", MarkKind.ExeString, false),
        }),

        // ---- Compiler / runtime --------------------------------------------
        new(BomCategory.Runtime, "Microsoft Visual C++ runtime", new[]
        {
            new Mark("msvcrt.dll", MarkKind.File, false), new Mark("msvcr71.dll", MarkKind.File, true),
            new Mark("msvcr80.dll", MarkKind.File, true), new Mark("msvcr90.dll", MarkKind.File, true),
            new Mark("msvcp60.dll", MarkKind.File, true), new Mark("mfc42.dll", MarkKind.File, false),
        }),
        new(BomCategory.Runtime, "Watcom C/C++", new[] { new Mark("WATCOM", MarkKind.ExeString, true) }),
        new(BomCategory.Runtime, "Borland/Turbo", new[] { new Mark("Borland", MarkKind.ExeString, true) }),
        new(BomCategory.Runtime, ".NET Framework", new[]
        {
            new Mark("mscoree.dll", MarkKind.File, true), new Mark("_CorExeMain", MarkKind.ExeString, true),
        }),

        // ---- Asset pipelines (platform tells) ------------------------------
        new(BomCategory.AssetPipeline, "PlayStation media (STR/XA/VAG)", new[]
        {
            new Mark(".str", MarkKind.Extension, false), new Mark(".xa", MarkKind.Extension, false),
            new Mark(".vag", MarkKind.Extension, true), new Mark(".vab", MarkKind.Extension, true),
            new Mark(".tim", MarkKind.Extension, true),
        }),

        // ---- Platform boot markers -----------------------------------------
        new(BomCategory.Platform, "Sony PlayStation", new[]
        {
            new Mark("SYSTEM.CNF", MarkKind.File, true), new Mark("PSX.EXE", MarkKind.File, true),
        }),
        new(BomCategory.Platform, "Sega (IP.BIN boot)", new[]
        {
            new Mark("IP.BIN", MarkKind.File, true),
        }),
    };

    // Runtime-version hints keyed on the DLL that was found.
    private static readonly Dictionary<string, string> MsvcVersions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msvcp60.dll"] = "6.0 (VC6)", ["msvcr71.dll"] = "7.1 (VC2003)", ["msvcr80.dll"] = "8.0 (VC2005)",
        ["msvcr90.dll"] = "9.0 (VC2008)", ["msvcr100.dll"] = "10.0 (VC2010)",
    };

    /// <summary>Build the bill-of-materials from a cooked ISO 9660 image.</summary>
    public static DiscBom FromIso(byte[] isoImage, int maxExeBytes = 4 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(isoImage);
        var paths = new List<string>();
        var binaries = new List<CopyProtectionCatalog.ScannedBinary>();
        string volumeId = "";

        try
        {
            IsoDirectory dir;
            using (var ms = new MemoryStream(isoImage, writable: false))
                dir = IsoReader.Read(ms);
            volumeId = dir.VolumeId ?? "";

            foreach (var f in dir.Files)
            {
                paths.Add(f.Path);
                string name = StripVersion(f.Path);
                if (!IsScannable(name)) continue;
                long start = (long)f.Extent * 2048;
                long len = Math.Min(f.Size, maxExeBytes);
                if (start < 0 || start + len > isoImage.Length || len <= 0) continue;
                binaries.Add(new CopyProtectionCatalog.ScannedBinary(name, isoImage.AsSpan((int)start, (int)len).ToArray()));
            }
        }
        catch { /* not a readable ISO — components only from whatever we gathered */ }

        MasteringReport? chronology = null;
        try { chronology = DiscChronology.Analyze(isoImage); } catch { }

        return Analyze(volumeId, paths, binaries, chronology);
    }

    public static DiscBom Analyze(string volumeId, IEnumerable<string> filePaths,
                                  IReadOnlyList<CopyProtectionCatalog.ScannedBinary> binaries,
                                  MasteringReport? chronology)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        var basenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var raw in filePaths)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            var segs = raw.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 1 < segs.Length; i++) dirs.Add(StripVersion(segs[i]));
            if (segs.Length == 0) continue;
            string n = StripVersion(segs[^1]);
            basenames.Add(n);
            names.Add(n);
        }

        var components = new List<TechComponent>();
        foreach (var comp in Catalog)
        {
            var evidence = new List<ProtectionEvidence>();
            bool strong = false, supporting = false;
            foreach (var m in comp.Marks)
            {
                bool hit = m.Kind switch
                {
                    MarkKind.File => basenames.Contains(m.Value),
                    MarkKind.Directory => dirs.Contains(m.Value),
                    MarkKind.Extension => names.Any(n => n.EndsWith(m.Value, StringComparison.OrdinalIgnoreCase)),
                    MarkKind.ExeString => binaries is not null && binaries.Any(b => ContainsAscii(b.Data, m.Value)),
                    _ => false,
                };
                if (!hit) continue;
                if (m.Strong) strong = true; else supporting = true;
                evidence.Add(new ProtectionEvidence(
                    m.Kind switch { MarkKind.File => "file", MarkKind.Directory => "directory",
                                    MarkKind.Extension => "extension", _ => "exe-signature" },
                    m.Value));
            }
            if (evidence.Count == 0) continue;

            var confidence = strong ? ProtectionConfidence.Confirmed
                           : supporting ? ProtectionConfidence.Likely : ProtectionConfidence.Possible;
            string? version = VersionFor(comp.Name, basenames);
            components.Add(new TechComponent(comp.Category, comp.Name, version, confidence, evidence));
        }

        components = components.OrderBy(c => c.Category).ThenBy(c => c.Name).ToList();

        string? buildDate = chronology?.VolumeCreated is { IsValid: true } vc ? vc.ToString() : null;
        string? earliest = chronology?.EarliestFile?.ToString();
        string? latest = chronology?.LatestFile?.ToString();
        int fileCount = chronology?.FileCount ?? names.Count;

        var notes = new List<string>();
        if (chronology?.LooksTampered == true)
            notes.Add("The disc's own timestamps show anomalies (see disc-date) — treat the build date with caution.");

        return new DiscBom
        {
            VolumeId = volumeId,
            BuildDate = buildDate,
            EarliestFile = earliest,
            LatestFile = latest,
            FileCount = fileCount,
            Components = components,
            Notes = notes,
        };
    }

    public static string Render(DiscBom bom)
    {
        var sb = new StringBuilder();
        sb.AppendLine(bom.Summary());
        if (bom.EarliestFile is not null && bom.LatestFile is not null)
            sb.AppendLine($"  file dates: {bom.EarliestFile} .. {bom.LatestFile} ({bom.FileCount:N0} files)");
        foreach (var c in bom.Components)
        {
            sb.AppendLine($"  {c}");
            foreach (var e in c.Evidence) sb.AppendLine($"      - {e.Kind}: {e.Detail}");
        }
        foreach (var n in bom.Notes) sb.AppendLine($"  note: {n}");
        return sb.ToString().TrimEnd();
    }

    // ---- internals ----------------------------------------------------------

    private static string? VersionFor(string component, HashSet<string> basenames)
    {
        if (component == "Microsoft Visual C++ runtime")
            foreach (var (dll, ver) in MsvcVersions)
                if (basenames.Contains(dll)) return ver;
        return null;
    }

    private static bool IsScannable(string name)
    {
        foreach (var ext in new[] { ".exe", ".dll", ".sys", ".ovl", ".elf", ".xbe", ".self" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string StripVersion(string segment)
    {
        int semi = segment.IndexOf(';');
        return (semi >= 0 ? segment[..semi] : segment).Trim();
    }

    private static bool ContainsAscii(byte[] haystack, string needle)
    {
        var n = Encoding.ASCII.GetBytes(needle);
        if (n.Length == 0 || haystack.Length < n.Length) return false;
        int last = haystack.Length - n.Length;
        for (int i = 0; i <= last; i++)
        {
            int j = 0;
            while (j < n.Length && haystack[i + j] == n[j]) j++;
            if (j == n.Length) return true;
        }
        return false;
    }
}
