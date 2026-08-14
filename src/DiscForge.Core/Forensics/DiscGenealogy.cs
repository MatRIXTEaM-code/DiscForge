// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Forensics;

/// <summary>How a disc was manufactured, as far as the evidence tells us.</summary>
public enum DiscMedia { Unknown, Pressed, Recordable }

/// <summary>An authenticity verdict for one disc — detection/assessment only, never a definitive legal claim.</summary>
public enum Authenticity { AuthenticPressing, Suspect, LikelyCounterfeit, Unknown }

/// <summary>One disc's provenance signals, gathered from ring codes, a physical fingerprint and the media type.</summary>
public sealed record DiscGenomeRecord
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    /// <summary>The stamped matrix / mastering code in the ring (e.g. "SN123456-01").</summary>
    public string? Matrix { get; init; }
    /// <summary>The mastering (glass-master) IFPI SID — "IFPI Lxxx". May be given with or without the prefix.</summary>
    public string? MasteringSid { get; init; }
    /// <summary>The mould (replication-line/plant) IFPI SID — "IFPI xxxx".</summary>
    public string? MouldSid { get; init; }
    public DiscMedia Media { get; init; } = DiscMedia.Unknown;
    /// <summary>Optional physical error-band profile (from a positional error scan) for copy-level linking.</summary>
    public IReadOnlyList<int>? Fingerprint { get; init; }
}

/// <summary>A pressing plant / replication line within a master family (shared mould SID).</summary>
public sealed record PlantBranch(string? MouldSid, IReadOnlyList<string> Members);

/// <summary>All discs sharing one glass master, broken down by pressing plant.</summary>
public sealed record MasterFamily
{
    public required string MasterKey { get; init; }
    public string? MasteringSid { get; init; }
    public string? Matrix { get; init; }
    public required IReadOnlyList<PlantBranch> Plants { get; init; }
    public required IReadOnlyList<string> Members { get; init; }
}

/// <summary>Two discs whose physical error maps are so alike they are almost certainly the same physical copy.</summary>
public sealed record PhysicalSibling(string A, string B, double Similarity);

/// <summary>The authenticity assessment for one disc and the evidence behind it.</summary>
public sealed record DiscVerdict
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public required Authenticity Authenticity { get; init; }
    public required IReadOnlyList<string> Reasons { get; init; }
}

/// <summary>The full genealogy of a collection: its master families, physical-copy links and authenticity verdicts.</summary>
public sealed record GenealogyReport
{
    public required IReadOnlyList<MasterFamily> Families { get; init; }
    public required IReadOnlyList<PhysicalSibling> SameCopyLinks { get; init; }
    public required IReadOnlyList<DiscVerdict> Verdicts { get; init; }
    /// <summary>Discs that share no master with any other — singletons (a unique title is not itself suspicious).</summary>
    public required IReadOnlyList<string> Singletons { get; init; }
}

/// <summary>
/// Weaves a whole collection's physical-provenance signals into one family tree. Each pressed disc carries
/// identifiers stamped into its ring: a <b>matrix</b> code, a <b>mastering (glass-master) IFPI SID</b>, and a
/// <b>mould (replication-line) IFPI SID</b>. Discs cut from the same glass master share the mastering SID;
/// discs stamped on the same line share the mould SID. Layering a <b>physical error-map fingerprint</b> on top
/// links individual copies, and the <b>media type</b> separates real pressings from recordable (CD-R/DVD-R)
/// burns. This groups the collection into master families → plant branches → individual copies, and flags each
/// disc's authenticity — a burned copy or a pressing missing the master identifiers its siblings carry stands
/// out. Detection and assessment only: it reports what the physical evidence shows and defeats nothing.
/// </summary>
public static class DiscGenealogy
{
    /// <summary>Above this profile correlation two discs are treated as the same physical copy.</summary>
    public const double SameCopyThreshold = 0.92;

    public static GenealogyReport Build(IReadOnlyList<DiscGenomeRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        // --- master families -------------------------------------------------
        var withKey = records.Select(r => (r, key: MasterKey(r))).ToList();
        var families = new List<MasterFamily>();
        var singletons = new List<string>();

        foreach (var grp in withKey.Where(x => x.key != null)
                                    .GroupBy(x => x.key!, StringComparer.OrdinalIgnoreCase))
        {
            var members = grp.Select(x => x.r).ToList();
            if (members.Count == 1)
            {
                singletons.Add(members[0].Id);
                continue;
            }
            var plants = members
                .GroupBy(m => NormSid(m.MouldSid), StringComparer.OrdinalIgnoreCase)
                .Select(g => new PlantBranch(g.Key, g.Select(m => m.Id).ToList()))
                .ToList();
            families.Add(new MasterFamily
            {
                MasterKey = grp.Key,
                MasteringSid = members.Select(m => NormSid(m.MasteringSid)).FirstOrDefault(s => s != null),
                Matrix = members.Select(m => m.Matrix).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
                Plants = plants,
                Members = members.Select(m => m.Id).ToList(),
            });
        }
        // Records with no master key at all are singletons too.
        singletons.AddRange(withKey.Where(x => x.key == null).Select(x => x.r.Id));

        // --- physical-copy links --------------------------------------------
        var links = new List<PhysicalSibling>();
        var fp = records.Where(r => r.Fingerprint is { Count: > 0 }).ToList();
        for (int i = 0; i < fp.Count; i++)
            for (int j = i + 1; j < fp.Count; j++)
            {
                double sim = ProfileSimilarity(fp[i].Fingerprint!, fp[j].Fingerprint!);
                if (sim >= SameCopyThreshold)
                    links.Add(new PhysicalSibling(fp[i].Id, fp[j].Id, Math.Round(sim, 4)));
            }

        // --- authenticity verdicts ------------------------------------------
        var familyHasMaster = families.ToDictionary(f => f.MasterKey, f => f.MasteringSid != null, StringComparer.OrdinalIgnoreCase);
        var verdicts = records.Select(r => Assess(r, MasterKey(r), familyHasMaster)).ToList();

        return new GenealogyReport
        {
            Families = families,
            SameCopyLinks = links,
            Verdicts = verdicts,
            Singletons = singletons,
        };
    }

    private static DiscVerdict Assess(DiscGenomeRecord r, string? masterKey, Dictionary<string, bool> familyHasMaster)
    {
        var reasons = new List<string>();
        Authenticity verdict;

        string? mastCode = SidCode(r.MasteringSid);
        string? mouldCode = SidCode(r.MouldSid);
        bool mastValid = mastCode != null && RingCodeParser.IsValidSid(mastCode, mastering: true);
        bool mouldValid = mouldCode != null && RingCodeParser.IsValidSid(mouldCode, mastering: false);

        if (r.Media == DiscMedia.Recordable)
        {
            verdict = Authenticity.LikelyCounterfeit;
            reasons.Add("Recordable media (CD-R/DVD-R) — a burned copy, not an original pressing.");
            if (mastCode != null) reasons.Add("A ring 'mastering SID' on recordable media is inconsistent — pressings, not burns, carry one.");
        }
        else if (mastValid)
        {
            verdict = Authenticity.AuthenticPressing;
            reasons.Add($"Carries a valid mastering SID (IFPI {mastCode}) — cut from a glass master.");
            if (mouldValid) reasons.Add($"Stamped on replication line IFPI {mouldCode}.");
            else if (mouldCode != null) reasons.Add($"Mould SID '{mouldCode}' is malformed.");
        }
        else if (mastCode != null && !mastValid)
        {
            verdict = Authenticity.Suspect;
            reasons.Add($"Mastering SID '{mastCode}' is malformed for a glass-master code (expected IFPI L###).");
        }
        else if (r.Media == DiscMedia.Pressed)
        {
            // Pressed but no mastering SID. If its family siblings have one, that absence is a red flag.
            bool siblingsHaveMaster = masterKey != null && familyHasMaster.TryGetValue(masterKey, out var h) && h;
            if (siblingsHaveMaster)
            {
                verdict = Authenticity.Suspect;
                reasons.Add("Pressed but missing the mastering SID that its master-family siblings carry.");
            }
            else if (!string.IsNullOrWhiteSpace(r.Matrix))
            {
                verdict = Authenticity.AuthenticPressing;
                reasons.Add($"Pressed disc with a stamped matrix ('{r.Matrix}') but no readable IFPI SID (common on older pressings).");
            }
            else
            {
                verdict = Authenticity.Suspect;
                reasons.Add("Pressed media but no mastering SID or matrix code could be read.");
            }
        }
        else
        {
            verdict = Authenticity.Unknown;
            reasons.Add("Not enough physical evidence (media type unknown, no readable ring codes) to assess.");
        }

        return new DiscVerdict { Id = r.Id, Title = r.Title, Authenticity = verdict, Reasons = reasons };
    }

    public static string Render(GenealogyReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Master families: {r.Families.Count}   physical-copy links: {r.SameCopyLinks.Count}   singletons: {r.Singletons.Count}");
        foreach (var f in r.Families)
        {
            sb.AppendLine($"● Master {f.MasterKey}" + (f.MasteringSid != null ? $" (IFPI {f.MasteringSid})" : "") +
                          $" — {f.Members.Count} disc(s)");
            foreach (var p in f.Plants)
                sb.AppendLine($"    ├ plant {(p.MouldSid ?? "?")} : {string.Join(", ", p.Members)}");
        }
        if (r.SameCopyLinks.Count > 0)
        {
            sb.AppendLine("Same-physical-copy links:");
            foreach (var l in r.SameCopyLinks) sb.AppendLine($"    {l.A} ≈ {l.B}  ({l.Similarity:P1})");
        }
        sb.AppendLine("Authenticity:");
        foreach (var v in r.Verdicts)
            sb.AppendLine($"    [{v.Authenticity}] {v.Id}{(v.Title != null ? $" — {v.Title}" : "")}: {v.Reasons.FirstOrDefault()}");
        return sb.ToString().TrimEnd();
    }

    // ---- helpers --------------------------------------------------------------

    /// <summary>The grouping key for a master family: the mastering SID if present, else the matrix code.</summary>
    private static string? MasterKey(DiscGenomeRecord r)
    {
        string? m = NormSid(r.MasteringSid);
        if (m != null) return "SID:" + m;
        if (!string.IsNullOrWhiteSpace(r.Matrix)) return "MX:" + NormMatrix(r.Matrix!);
        return null;
    }

    /// <summary>Normalise a SID to its bare code (strip "IFPI ", upper-case), or null.</summary>
    private static string? NormSid(string? s)
    {
        var parsed = s is null ? null : RingCodeParser.ParseSid(s);
        return parsed?.Code.ToUpperInvariant();
    }

    private static string? SidCode(string? s) => NormSid(s);

    private static string NormMatrix(string m) =>
        new string(m.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

    /// <summary>Cosine similarity of two error-band profiles (0..1); a value near 1 means near-identical error maps.</summary>
    private static double ProfileSimilarity(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        int n = Math.Min(a.Count, b.Count);
        if (n == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < n; i++)
        {
            double x = a[i], y = b[i];
            dot += x * y; na += x * x; nb += y * y;
        }
        if (na == 0 || nb == 0) return na == nb ? 1.0 : 0.0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
