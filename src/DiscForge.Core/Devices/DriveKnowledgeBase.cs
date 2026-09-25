// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;

namespace DiscForge.Core.Devices;

/// <summary>Which read command the preservation community prefers on this drive for audio.</summary>
public enum PreferredReadCommand
{
    /// <summary>Standard MMC READ CD (0xBE) — every compliant drive.</summary>
    ReadCdBE,
    /// <summary>The Plextor vendor READ CD-DA (0xD8) — the command behind lead-in /
    /// negative-LBA capture on the classic Plextor CD units. DiscForge does not issue
    /// 0xD8 yet; the preference is recorded so the profile says what the drive is FOR.</summary>
    PlextorD8,
}

/// <summary>
/// One drive family's community-reference data sheet: the values that cannot be
/// probed from the drive alone (read offset, overread reach, C2 reputation) as the
/// preservation community has established them. Every entry carries its sources,
/// and every value is REFERENCE data — a starting point the live probes and a
/// secure rip then confirm, never a substitute for confirmation.
/// </summary>
public sealed record DriveReference
{
    /// <summary>INQUIRY vendor to match, normalized (see <see cref="DriveKnowledgeBase.Normalize"/>).</summary>
    public required string Vendor { get; init; }
    /// <summary>Substring of the normalized INQUIRY model that identifies the family.</summary>
    public required string ModelContains { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>Combined read offset in samples, AccurateRip sign convention
    /// (positive = the drive reads early; correction shifts by +N samples).</summary>
    public int? ReadOffsetSamples { get; init; }

    /// <summary>Can it read into the lead-in (negative LBA / track 1 pregap territory)?</summary>
    public ProbeState LeadInOverread { get; init; } = ProbeState.NotDetermined;
    /// <summary>Can it read past the lead-out edge?</summary>
    public ProbeState LeadOutOverread { get; init; } = ProbeState.NotDetermined;

    /// <summary>The community's verdict on whether this family's C2 pointers are trustworthy.</summary>
    public ProbeState C2Reputation { get; init; } = ProbeState.NotDetermined;

    public PreferredReadCommand PreferredRead { get; init; } = PreferredReadCommand.ReadCdBE;

    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>Where each claim comes from. Reference data without provenance is rumour.</summary>
    public required IReadOnlyList<string> Sources { get; init; }

    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{DisplayName}  (matches {(Vendor.Length == 0 ? "any vendor" : Vendor)} *{ModelContains}*)");
        sb.AppendLine($"  read offset      : {(ReadOffsetSamples is int o ? $"{o:+#;-#;0} samples" : "not on record")}" +
                      "  [reference — confirm with `read-offset` / a secure rip]");
        sb.AppendLine($"  lead-in overread : {Describe(LeadInOverread)}");
        sb.AppendLine($"  lead-out overread: {Describe(LeadOutOverread)}");
        sb.AppendLine($"  C2 reputation    : {Describe(C2Reputation)}");
        sb.AppendLine($"  preferred read   : {(PreferredRead == PreferredReadCommand.PlextorD8 ? "Plextor 0xD8 (vendor CD-DA)" : "READ CD 0xBE")}");
        foreach (var n in Notes) sb.AppendLine($"  note: {n}");
        sb.Append($"  sources: {string.Join("; ", Sources)}");
        return sb.ToString();
    }

    private static string Describe(ProbeState s) => s switch
    {
        ProbeState.Yes => "yes (community-established)",
        ProbeState.No => "no (community-established)",
        ProbeState.Advertised => "advertised",
        ProbeState.NotProbed => "not probed",
        _ => "not on record",
    };
}

/// <summary>
/// The bundled drive knowledge base: community-established reference data for
/// drives the preservation scene has already measured to death, keyed by INQUIRY
/// vendor/model. Deliberately small and deliberately sourced — an entry ships only
/// when the values are settled community knowledge (AccurateRip's offset database,
/// the Redump wiki's drive pages), and everything it supplies is labelled
/// reference data for the live probes to confirm, in the same spirit as
/// <see cref="DriveProfile"/>: pre-filled, never fabricated.
/// </summary>
public static class DriveKnowledgeBase
{
    public static IReadOnlyList<DriveReference> All { get; } = new List<DriveReference>
    {
        new()
        {
            Vendor = "PLEXTOR",
            ModelContains = "PX-W5224",
            DisplayName = "Plextor PX-W5224A / PX-W5224TA (52/24/52 CD-RW)",
            ReadOffsetSamples = +30,
            LeadInOverread = ProbeState.Yes,
            LeadOutOverread = ProbeState.Yes,
            C2Reputation = ProbeState.Yes,
            PreferredRead = PreferredReadCommand.PlextorD8,
            Notes =
            [
                "A community reference CD dumper: lead-in (negative LBA) and lead-out capture " +
                    "are this family's signature — the reason preservationists still hunt them.",
                "The TA suffix is the ATAPI variant; INQUIRY typically reports the model as PX-W5224A.",
                "Lead-in capture uses the vendor 0xD8 read + cache technique (as in redumper); " +
                    "standard 0xBE extraction works fully within the program area.",
            ],
            Sources =
            [
                "AccurateRip drive offset database (Plextor CD-RW family: +30)",
                "Redump wiki — drive compatibility pages",
                "redumper documentation (Plextor lead-in/lead-out method)",
            ],
        },
        new()
        {
            Vendor = "PLEXTOR",
            ModelContains = "PREMIUM",
            DisplayName = "Plextor Premium / Premium2 (CD-RW)",
            ReadOffsetSamples = +30,
            LeadInOverread = ProbeState.Yes,
            LeadOutOverread = ProbeState.Yes,
            C2Reputation = ProbeState.Yes,
            PreferredRead = PreferredReadCommand.PlextorD8,
            Notes = ["Same classic-Plextor dumping pedigree as the PX-W52xx family."],
            Sources =
            [
                "AccurateRip drive offset database (+30)",
                "Redump wiki — drive compatibility pages",
            ],
        },
        new()
        {
            Vendor = "ASUS",
            ModelContains = "BW-16D1HT",
            DisplayName = "ASUS BW-16D1HT (BD-RE)",
            ReadOffsetSamples = +6,
            LeadInOverread = ProbeState.No,
            LeadOutOverread = ProbeState.NotDetermined,
            C2Reputation = ProbeState.Advertised,
            PreferredRead = PreferredReadCommand.ReadCdBE,
            Notes = ["A modern MediaTek-chipset workhorse — fine for program-area CD/DVD/BD work; " +
                     "no classic-Plextor lead-in tricks."],
            Sources = ["AccurateRip drive offset database (+6)"],
        },
        new()
        {
            // LiteOn units often report the generic "ATAPI" as INQUIRY vendor,
            // so this entry matches on the model string alone.
            Vendor = "",
            ModelContains = "IHAS124",
            DisplayName = "LiteOn iHAS124 (DVD-RW)",
            ReadOffsetSamples = +6,
            LeadInOverread = ProbeState.No,
            LeadOutOverread = ProbeState.NotDetermined,
            C2Reputation = ProbeState.Advertised,
            PreferredRead = PreferredReadCommand.ReadCdBE,
            Sources = ["AccurateRip drive offset database (+6)"],
        },

        // --- 2026-08-29 growth batch ------------------------------------------------
        // Sourced from the AccurateRip community offset table (mirrored, with per-model submission
        // counts, in DiscImageCreator's driveOffset.txt). Only the offset is community-established
        // for these; none of them are documented classic-Plextor-style lead-in/lead-out dumpers, so
        // that half is left honestly undetermined rather than assumed from the Plextor entries above.
        new()
        {
            Vendor = "HL-DT-ST",
            ModelContains = "WH16NS40",
            DisplayName = "LG WH16NS40 (BD-RE, internal SATA)",
            ReadOffsetSamples = +6,
            LeadInOverread = ProbeState.No,
            LeadOutOverread = ProbeState.NotDetermined,
            C2Reputation = ProbeState.Advertised,
            PreferredRead = PreferredReadCommand.ReadCdBE,
            Notes = ["One of the most common current internal BD burners — a solid program-area " +
                     "reader/writer, no classic-Plextor overread tricks."],
            Sources = ["AccurateRip drive offset database (+6, 1199 submissions agreeing 100%)"],
        },
        new()
        {
            Vendor = "HL-DT-ST",
            ModelContains = "GH24NSC0",
            DisplayName = "LG GH24NSC0 (DVD-RW, internal SATA)",
            ReadOffsetSamples = +6,
            LeadInOverread = ProbeState.No,
            LeadOutOverread = ProbeState.NotDetermined,
            C2Reputation = ProbeState.Advertised,
            PreferredRead = PreferredReadCommand.ReadCdBE,
            Notes = ["The commodity internal DVD burner of the 2010s-2020s — still widely sold new."],
            Sources = ["AccurateRip drive offset database (+6, 859 submissions agreeing 100%)"],
        },
        new()
        {
            Vendor = "PIONEER",
            ModelContains = "BDR-209",
            DisplayName = "Pioneer BDR-209 family (BD-RW)",
            ReadOffsetSamples = +667,
            LeadInOverread = ProbeState.NotDetermined,
            LeadOutOverread = ProbeState.NotDetermined,
            C2Reputation = ProbeState.Advertised,
            PreferredRead = PreferredReadCommand.ReadCdBE,
            Notes = ["Pioneer's offset is one of the largest in common circulation (+667) — worth " +
                     "flagging so an un-corrected rip on this family isn't mistaken for disc damage."],
            Sources = ["AccurateRip drive offset database (+667, 2000+ submissions across " +
                       "BDR-209/209D/209M/209MIO agreeing 100%)"],
        },
        new()
        {
            Vendor = "ASUS",
            ModelContains = "DRW-24B1ST",
            DisplayName = "ASUS DRW-24B1ST (DVD-RW)",
            ReadOffsetSamples = +6,
            LeadInOverread = ProbeState.No,
            LeadOutOverread = ProbeState.NotDetermined,
            C2Reputation = ProbeState.Advertised,
            PreferredRead = PreferredReadCommand.ReadCdBE,
            Notes = ["Ubiquitous budget internal burner — near-identical offset behaviour to the " +
                     "existing BW-16D1HT and iHAS124 entries above."],
            Sources = ["AccurateRip drive offset database (+6, ~2600 submissions across suffix " +
                       "variants a/c/g/i/j agreeing 100%)"],
        },
        new()
        {
            Vendor = "TSSTCORP",
            ModelContains = "SH-224",
            DisplayName = "Samsung/TSST SH-224 family (DVD-RW)",
            ReadOffsetSamples = +6,
            LeadInOverread = ProbeState.No,
            LeadOutOverread = ProbeState.NotDetermined,
            C2Reputation = ProbeState.Advertised,
            PreferredRead = PreferredReadCommand.ReadCdBE,
            Notes = ["TSSTcorp is the INQUIRY vendor string these Samsung-branded drives actually " +
                     "report."],
            Sources = ["AccurateRip drive offset database (+6, 3000+ submissions across " +
                       "SH-224BB/DB/FB/GB agreeing 100%)"],
        },
        new()
        {
            Vendor = "PLEXTOR",
            ModelContains = "PX-716",
            DisplayName = "Plextor PX-716A/AL (DVD±R DL)",
            ReadOffsetSamples = +30,
            LeadInOverread = ProbeState.NotDetermined,
            LeadOutOverread = ProbeState.NotDetermined,
            C2Reputation = ProbeState.Advertised,
            PreferredRead = PreferredReadCommand.ReadCdBE,
            Notes = ["A Plextor DVD writer, not the classic CD-only Premium/PX-W52xx lineage — shares " +
                     "the family's +30 offset but its lead-in/lead-out overread reach isn't on record " +
                     "here, so it isn't assumed from the CD-RW entries above."],
            Sources = ["AccurateRip drive offset database (+30, 2690+223 submissions across " +
                       "PX-716A/AL agreeing 100%)"],
        },
        new()
        {
            // Sony's own INQUIRY vendor for these is "SONY "; the identical Optiarc-branded OEM
            // unit (same drive, different label) reports "Optiarc" — match on vendor-agnostic model.
            Vendor = "",
            ModelContains = "AD-7200",
            DisplayName = "Sony/Optiarc AD-7200A/S (DVD±RW)",
            ReadOffsetSamples = +48,
            LeadInOverread = ProbeState.No,
            LeadOutOverread = ProbeState.NotDetermined,
            C2Reputation = ProbeState.Advertised,
            PreferredRead = PreferredReadCommand.ReadCdBE,
            Notes = ["Common 2008-era slimline/desktop burner; Sony- and Optiarc-branded units are " +
                     "the same hardware."],
            Sources = ["AccurateRip drive offset database (+48, ~370 submissions across " +
                       "SONY/Optiarc AD-7200A/AD-7200S agreeing 100%)"],
        },
    };

    /// <summary>Look a live drive up by its INQUIRY strings. Null when the drive
    /// isn't in the book — which means "unknown", never "unsuitable".</summary>
    public static DriveReference? Find(string vendor, string model)
    {
        string v = Normalize(vendor), m = Normalize(model);
        return All.FirstOrDefault(r =>
            (r.Vendor.Length == 0 || v.Contains(Normalize(r.Vendor), StringComparison.Ordinal))
            && m.Contains(Normalize(r.ModelContains), StringComparison.Ordinal));
    }

    /// <summary>Free-text lookup (CLI `drive-db <text>`): match against vendor, model or display name.</summary>
    public static IReadOnlyList<DriveReference> Search(string text)
    {
        string t = Normalize(text);
        return All.Where(r => Normalize(r.Vendor).Contains(t, StringComparison.Ordinal)
                           || Normalize(r.ModelContains).Contains(t, StringComparison.Ordinal)
                           || Normalize(r.DisplayName).Contains(t, StringComparison.Ordinal))
                  .ToList();
    }

    /// <summary>Uppercase, strip everything that isn't a letter or digit — INQUIRY strings
    /// pad with spaces and vary punctuation ("PX-W5224A" vs "PX W5224A").</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
        return sb.ToString();
    }
}
