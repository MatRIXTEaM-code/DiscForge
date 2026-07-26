// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Xml.Linq;

namespace DiscForge.Core.Dat;

/// <summary>One known-good file (a "rom") from a DAT — a disc track, a cue sheet,
/// or a whole image, depending on the system.</summary>
public sealed record DatRom
{
    public required string Game { get; init; }
    public required string Name { get; init; }
    public long Size { get; init; }
    public string? Crc { get; init; }
    public string? Md5 { get; init; }
    public string? Sha1 { get; init; }
    /// <summary>The parent game name when this game is a clone (from the DAT's
    /// <c>cloneof</c> attribute); null for a parent or a flat DAT.</summary>
    public string? CloneOf { get; init; }
    /// <summary>The game's <c>&lt;description&gt;</c> text when present; else null.</summary>
    public string? Description { get; init; }
}

/// <summary>The outcome of checking a file against a DAT.</summary>
public sealed record DatMatch
{
    public required bool Verified { get; init; }
    public DatRom? Rom { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// Parses a Logiqx-XML DAT file — the datfiles the disc-preservation databases
/// (redump.org and the like) publish — and verifies a dump against it. Each DAT
/// lists the known-good files of every catalogued disc with their size and CRC-32
/// / MD5 / SHA-1, so a file whose hashes match a DAT entry is a confirmed-good
/// dump. DiscForge computes those same hashes, so this closes the loop: is this
/// image exactly the preserved one, and which disc is it?
/// </summary>
public sealed class DatFile
{
    private readonly List<DatRom> _roms;
    private readonly Dictionary<string, List<DatRom>> _byCrc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DatRom>> _bySha1 = new(StringComparer.OrdinalIgnoreCase);

    public string? Name { get; }
    public IReadOnlyList<DatRom> Roms => _roms;
    public int Count => _roms.Count;

    private DatFile(string? name, List<DatRom> roms)
    {
        Name = name;
        _roms = roms;
        foreach (var r in roms)
        {
            if (r.Crc is not null) Index(_byCrc, r.Crc, r);
            if (r.Sha1 is not null) Index(_bySha1, r.Sha1, r);
        }
    }

    private static void Index(Dictionary<string, List<DatRom>> map, string key, DatRom r)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<DatRom>();
        list.Add(r);
    }

    public static DatFile Parse(Stream xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        return FromDocument(XDocument.Load(xml));
    }

    public static DatFile ParseText(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        return FromDocument(XDocument.Parse(xml));
    }

    private static DatFile FromDocument(XDocument doc)
    {
        var root = doc.Root ?? throw new FormatException("The DAT file is empty.");
        string? name = root.Element("header")?.Element("name")?.Value;

        var roms = new List<DatRom>();
        // Redump uses <game>; some DATs use <machine>. Accept both.
        foreach (var game in root.Elements().Where(e => e.Name.LocalName is "game" or "machine"))
        {
            string gameName = (string?)game.Attribute("name") ?? "";
            // No-Intro/Redump clone links; accept cloneof (and romof as a fallback).
            string? cloneOf = (string?)game.Attribute("cloneof") ?? (string?)game.Attribute("romof");
            string? description = game.Element("description")?.Value;
            if (string.IsNullOrWhiteSpace(cloneOf)) cloneOf = null;
            if (string.IsNullOrWhiteSpace(description)) description = null;
            foreach (var rom in game.Elements("rom"))
            {
                roms.Add(new DatRom
                {
                    Game = gameName,
                    Name = (string?)rom.Attribute("name") ?? "",
                    Size = long.TryParse((string?)rom.Attribute("size"), out var s) ? s : 0,
                    Crc = Norm(rom.Attribute("crc")),
                    Md5 = Norm(rom.Attribute("md5")),
                    Sha1 = Norm(rom.Attribute("sha1")),
                    CloneOf = cloneOf,
                    Description = description,
                });
            }
        }
        return new DatFile(name, roms);
    }

    public IReadOnlyList<DatRom> ByCrc(string crc) =>
        _byCrc.TryGetValue(crc.Trim(), out var l) ? l : Array.Empty<DatRom>();

    public IReadOnlyList<DatRom> BySha1(string sha1) =>
        _bySha1.TryGetValue(sha1.Trim(), out var l) ? l : Array.Empty<DatRom>();

    /// <summary>
    /// Check a file's size and hashes against the DAT. A CRC-32 hit confirmed by a
    /// matching SHA-1 is a verified good dump; a CRC hit whose SHA-1 disagrees is
    /// flagged (a hash collision or a subtly different file); no hit means the file
    /// is not the catalogued dump.
    /// </summary>
    public DatMatch Verify(long size, string? crc, string? sha1 = null, string? md5 = null)
    {
        // Prefer SHA-1 (collision-resistant); fall back to CRC-32 (what most DATs key on).
        if (sha1 is not null)
        {
            foreach (var r in BySha1(sha1))
                if (r.Size == 0 || r.Size == size)
                    return new DatMatch { Verified = true, Rom = r, Reason = "SHA-1 and size match a catalogued dump." };
        }

        if (crc is not null)
        {
            var crcHits = ByCrc(crc);
            foreach (var r in crcHits)
            {
                bool sizeOk = r.Size == 0 || r.Size == size;
                bool sha1Ok = sha1 is null || r.Sha1 is null || string.Equals(r.Sha1, sha1, StringComparison.OrdinalIgnoreCase);
                if (sizeOk && sha1Ok)
                    return new DatMatch { Verified = true, Rom = r, Reason = "CRC-32 and size match a catalogued dump." };
            }
            if (crcHits.Count > 0)
                return new DatMatch
                {
                    Verified = false, Rom = crcHits[0],
                    Reason = "CRC-32 matches a catalogued dump but the SHA-1 or size differs — " +
                             "possibly a hash collision or a subtly altered file.",
                };
        }

        return new DatMatch { Verified = false, Reason = "No catalogued dump matches this file's hashes." };
    }

    private static string? Norm(XAttribute? a) =>
        string.IsNullOrWhiteSpace(a?.Value) ? null : a!.Value.Trim().ToLowerInvariant();
}
