// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.BluRay;

/// <summary>One enumerated Blu-ray title: a playlist plus the clip-info of the
/// clips it references, correlated by clip id.</summary>
public sealed record BluRayTitle
{
    /// <summary>The playlist file name, e.g. "00000.mpls".</summary>
    public required string PlaylistFile { get; init; }
    public required BluRayPlaylist Playlist { get; init; }
    /// <summary>Clip-info per referenced clip id (only those found in CLIPINF).</summary>
    public required IReadOnlyDictionary<string, BluRayClip> Clips { get; init; }

    public TimeSpan Duration => Playlist.TotalDuration;
    public int ChapterCount => Playlist.Chapters.Count;
    /// <summary>Distinct clip ids the playlist plays, in first-seen order.</summary>
    public IReadOnlyList<string> ClipIds =>
        Playlist.Items.Select(i => i.ClipId).Distinct().ToList();
}

/// <summary>
/// The convenience façade over the BDMV readers: point it at a single .mpls/.clpi
/// file, or at a whole BDMV folder to enumerate every title and correlate each
/// playlist to the clip-info of the clips it plays (PLAYLIST/*.mpls ↔
/// CLIPINF/*.clpi by the shared 5-digit clip id).
/// </summary>
public static class BdmvReader
{
    /// <summary>Parse a .mpls playlist file.</summary>
    public static BluRayPlaylist ReadPlaylist(string path) => MplsReader.ReadFile(path);

    /// <summary>Parse a .clpi clip-info file.</summary>
    public static BluRayClip ReadClip(string path) => ClpiReader.ReadFile(path);

    /// <summary>
    /// Enumerate the titles of a Blu-ray by reading BDMV/PLAYLIST/*.mpls and
    /// correlating each to BDMV/CLIPINF/&lt;clipid&gt;.clpi. Accepts either the
    /// disc root (containing a BDMV folder) or the BDMV folder itself. Titles come
    /// back sorted by playlist file name; a clip whose .clpi is missing is simply
    /// omitted from that title's clip map rather than failing the whole scan.
    /// </summary>
    public static IReadOnlyList<BluRayTitle> EnumerateTitles(string bdmvFolderPath)
    {
        ArgumentNullException.ThrowIfNull(bdmvFolderPath);
        if (!Directory.Exists(bdmvFolderPath))
            throw new BluRayFormatException($"BDMV folder not found: {bdmvFolderPath}");

        string bdmv = ResolveBdmv(bdmvFolderPath);
        string playlistDir = Path.Combine(bdmv, "PLAYLIST");
        string clipDir = Path.Combine(bdmv, "CLIPINF");
        if (!Directory.Exists(playlistDir))
            throw new BluRayFormatException($"No PLAYLIST folder under '{bdmv}'.");

        var titles = new List<BluRayTitle>();
        foreach (var mpls in Directory.EnumerateFiles(playlistDir, "*.mpls")
                                      .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var playlist = MplsReader.ReadFile(mpls);

            var clips = new Dictionary<string, BluRayClip>(StringComparer.OrdinalIgnoreCase);
            foreach (var clipId in playlist.Items.Select(i => i.ClipId).Distinct())
            {
                string clpi = Path.Combine(clipDir, clipId + ".clpi");
                if (File.Exists(clpi))
                    clips[clipId] = ClpiReader.ReadFile(clpi);
            }

            titles.Add(new BluRayTitle
            {
                PlaylistFile = Path.GetFileName(mpls),
                Playlist = playlist,
                Clips = clips,
            });
        }
        return titles;
    }

    /// <summary>Accept a disc root that holds a BDMV folder, or the BDMV folder itself.</summary>
    private static string ResolveBdmv(string path)
    {
        if (Directory.Exists(Path.Combine(path, "PLAYLIST"))) return path;
        string nested = Path.Combine(path, "BDMV");
        if (Directory.Exists(nested)) return nested;
        return path;
    }
}
