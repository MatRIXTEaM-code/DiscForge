// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace DiscForge.Core.Frontend;

/// <summary>One game as a front-end / emulator playlist sees it.</summary>
public sealed record PlaylistItem
{
    /// <summary>The path (absolute, or relative to where the playlist will live) of the image.</summary>
    public required string Path { get; init; }
    /// <summary>The display name — a DAT game title, else the file stem.</summary>
    public required string Label { get; init; }
    /// <summary>8-hex-digit CRC-32, or empty when unknown. Used by RetroArch's scanner match.</summary>
    public string Crc32Hex { get; init; } = "";
    /// <summary>The detected system, when known (e.g. "Sony - PlayStation"). Informational.</summary>
    public string System { get; init; } = "";
}

/// <summary>
/// Turns a set of DiscForge-identified images into the metadata files the popular
/// front-ends and emulators consume, so a verified collection drops straight into a
/// library: RetroArch playlists (<c>.lpl</c>, the modern JSON form), EmulationStation
/// / RetroBat <c>gamelist.xml</c>, and the multi-disc <c>.m3u</c> convention that
/// RetroArch, DuckStation and PCSX2 all use to swap discs and share one memory card.
///
/// This is pure cataloguing — DiscForge already knows what each file is and its
/// CRC-32; this just writes those facts in each front-end's dialect. Nothing here
/// touches protection, and it produces only playlist/metadata text, never modified
/// game data.
/// </summary>
public static class FrontendExport
{
    /// <summary>
    /// A multi-disc M3U: one image path per line, in order. Emulators load the M3U
    /// instead of a single disc so disc-swapping and a shared memory card work. Blank
    /// and whitespace-only paths are skipped; lines are LF-terminated.
    /// </summary>
    public static string BuildM3u(IEnumerable<string> discPaths)
    {
        ArgumentNullException.ThrowIfNull(discPaths);
        var sb = new StringBuilder();
        foreach (var p in discPaths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            sb.Append(p.Trim()).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// A RetroArch playlist (<c>.lpl</c>) in the current JSON format. Cores are left as
    /// <c>DETECT</c> so RetroArch picks one; <paramref name="playlistName"/> is the file
    /// the playlist will be saved as (RetroArch stores it back in each item's db_name).
    /// </summary>
    public static string BuildRetroArchLpl(string playlistName, IEnumerable<PlaylistItem> items)
    {
        ArgumentNullException.ThrowIfNull(playlistName);
        ArgumentNullException.ThrowIfNull(items);
        string db = playlistName.EndsWith(".lpl", StringComparison.OrdinalIgnoreCase) ? playlistName : playlistName + ".lpl";

        var lpl = new Lpl
        {
            Items = items.Select(i => new LplItem
            {
                Path = i.Path,
                Label = i.Label,
                CorePath = "DETECT",
                CoreName = "DETECT",
                Crc32 = (string.IsNullOrEmpty(i.Crc32Hex) ? "00000000" : i.Crc32Hex.ToUpperInvariant()) + "|crc",
                DbName = db,
            }).ToList(),
        };

        return JsonSerializer.Serialize(lpl, LplJsonOptions);
    }

    /// <summary>
    /// An EmulationStation / RetroBat <c>gamelist.xml</c>. Paths are emitted relative
    /// (<c>./name.ext</c>) when the item path is a bare file name, so the list is
    /// portable inside its ROM folder; an already-relative or absolute path is kept.
    /// </summary>
    public static string BuildEmulationStationGamelist(IEnumerable<PlaylistItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        // Omit the writer's own declaration (it would claim utf-16, the StringBuilder's
        // in-memory encoding) and prepend a plain one, so the text stays correct once
        // written to a UTF-8 file — which is what EmulationStation expects.
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?>\n");
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = true };
        using (var w = XmlWriter.Create(sb, settings))
        {
            w.WriteStartElement("gameList");
            foreach (var i in items)
            {
                w.WriteStartElement("game");
                w.WriteElementString("path", ToGamelistPath(i.Path));
                w.WriteElementString("name", i.Label);
                if (!string.IsNullOrEmpty(i.System))
                    w.WriteElementString("desc", i.System);
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndDocument();
        }
        return sb.ToString();
    }

    private static string ToGamelistPath(string path)
    {
        // A bare file name (no directory separator) becomes "./name" per ES convention.
        bool hasDir = path.Contains('/') || path.Contains('\\');
        return hasDir ? path : "./" + path;
    }

    // ---- RetroArch LPL JSON shape ----

    private static readonly JsonSerializerOptions LplJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class Lpl
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "1.5";
        [JsonPropertyName("default_core_path")] public string DefaultCorePath { get; set; } = "";
        [JsonPropertyName("default_core_name")] public string DefaultCoreName { get; set; } = "";
        [JsonPropertyName("label_display_mode")] public int LabelDisplayMode { get; set; }
        [JsonPropertyName("right_thumbnail_mode")] public int RightThumbnailMode { get; set; }
        [JsonPropertyName("left_thumbnail_mode")] public int LeftThumbnailMode { get; set; }
        [JsonPropertyName("sort_mode")] public int SortMode { get; set; }
        [JsonPropertyName("items")] public List<LplItem> Items { get; set; } = new();
    }

    private sealed class LplItem
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("label")] public string Label { get; set; } = "";
        [JsonPropertyName("core_path")] public string CorePath { get; set; } = "DETECT";
        [JsonPropertyName("core_name")] public string CoreName { get; set; } = "DETECT";
        [JsonPropertyName("crc32")] public string Crc32 { get; set; } = "00000000|crc";
        [JsonPropertyName("db_name")] public string DbName { get; set; } = "";
    }
}
