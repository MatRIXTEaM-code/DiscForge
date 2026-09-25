// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Globalization;
using System.Text;

namespace DiscForge.Core.Cue;

/// <summary>CD time code: minutes:seconds:frames, 75 frames per second,
/// one frame == one sector.</summary>
public readonly record struct Msf(int Minutes, int Seconds, int Frames)
{
    public static Msf FromSectors(long sectors)
    {
        if (sectors < 0) throw new ArgumentOutOfRangeException(nameof(sectors));
        int f = (int)(sectors % 75);
        long s = sectors / 75;
        int sec = (int)(s % 60);
        int min = (int)(s / 60);
        return new Msf(min, sec, f);
    }

    public long ToSectors() => (Minutes * 60L + Seconds) * 75 + Frames;

    public override string ToString() =>
        $"{Minutes:D2}:{Seconds:D2}:{Frames:D2}";

    public static Msf Parse(string s)
    {
        var parts = s.Split(':');
        if (parts.Length != 3) throw new FormatException($"Bad MSF '{s}'.");
        return new Msf(
            int.Parse(parts[0], CultureInfo.InvariantCulture),
            int.Parse(parts[1], CultureInfo.InvariantCulture),
            int.Parse(parts[2], CultureInfo.InvariantCulture));
    }
}

/// <summary>CUE track type token. Maps to a (mode, stored sector size) pair.</summary>
public enum CueTrackType
{
    Audio,
    Mode1_2048,
    Mode1_2352,
    Mode2_2336,
    Mode2_2352,
}

public sealed record CueIndex(int Number, Msf Time);

/// <summary>CUE FLAGS values (track control bits).</summary>
[Flags]
public enum CueFlags
{
    None = 0,
    /// <summary>DCP — digital copy permitted.</summary>
    Dcp = 1,
    /// <summary>4CH — four-channel audio.</summary>
    FourChannel = 2,
    /// <summary>PRE — audio recorded with pre-emphasis.</summary>
    PreEmphasis = 4,
    /// <summary>SCMS — serial copy management (stored; the burner treats it as
    /// informational, since SCMS is expressed by alternating the DCP bit).</summary>
    Scms = 8,
}

public sealed record CueTrack
{
    public required int Number { get; init; }
    public required CueTrackType Type { get; init; }
    /// <summary>The data file this track's sectors live in (multi-FILE style).</summary>
    public required string File { get; init; }
    /// <summary>Pregap declared via PREGAP (generated, not stored in FILE).</summary>
    public Msf? Pregap { get; init; }
    /// <summary>Silence appended after the track via POSTGAP (generated).</summary>
    public Msf? Postgap { get; init; }
    public required IReadOnlyList<CueIndex> Indices { get; init; }
    /// <summary>1-based session this track belongs to. Standard cue sheets carry no
    /// session concept, but Redump marks the second session of a multisession disc
    /// (e.g. a Dreamcast MIL-CD self-boot CD-ROM) with a "REM SESSION 02" line;
    /// tracks default to session 1 when no such marker precedes them.</summary>
    public int Session { get; init; } = 1;
    public CueFlags Flags { get; init; }
    /// <summary>12-character ISRC, if declared.</summary>
    public string? Isrc { get; init; }
    /// <summary>CD-TEXT TITLE for this track.</summary>
    public string? Title { get; init; }
    /// <summary>CD-TEXT PERFORMER for this track.</summary>
    public string? Performer { get; init; }
}

public sealed record CueSheet
{
    public required IReadOnlyList<CueTrack> Tracks { get; init; }
    /// <summary>CATALOG — the 13-digit media catalog number (MCN/EAN).</summary>
    public string? Catalog { get; init; }
    /// <summary>CD-TEXT TITLE at disc level (album title).</summary>
    public string? Title { get; init; }
    /// <summary>CD-TEXT PERFORMER at disc level (album artist).</summary>
    public string? Performer { get; init; }

    public static (string token, int sectorSize) TypeToToken(CueTrackType t) => t switch
    {
        CueTrackType.Audio => ("AUDIO", 2352),
        CueTrackType.Mode1_2048 => ("MODE1/2048", 2048),
        CueTrackType.Mode1_2352 => ("MODE1/2352", 2352),
        CueTrackType.Mode2_2336 => ("MODE2/2336", 2336),
        CueTrackType.Mode2_2352 => ("MODE2/2352", 2352),
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    public static CueTrackType TokenToType(string token) => token.ToUpperInvariant() switch
    {
        "AUDIO" => CueTrackType.Audio,
        "MODE1/2048" => CueTrackType.Mode1_2048,
        "MODE1/2352" => CueTrackType.Mode1_2352,
        "MODE2/2336" => CueTrackType.Mode2_2336,
        "MODE2/2352" => CueTrackType.Mode2_2352,
        _ => throw new FormatException($"Unsupported CUE track type '{token}'."),
    };

    public string Write()
    {
        var sb = new StringBuilder();
        if (Catalog is not null) sb.Append("CATALOG ").Append(Catalog).Append('\n');
        if (Title is not null) sb.Append("TITLE \"").Append(Title).Append("\"\n");
        if (Performer is not null) sb.Append("PERFORMER \"").Append(Performer).Append("\"\n");
        string? lastFile = null;
        foreach (var t in Tracks)
        {
            var (token, _) = TypeToToken(t.Type);
            // Emit FILE only when it changes: a single-file (merged) image gets
            // one FILE line with every TRACK under it, while a one-file-per-track
            // set repeats FILE — both the shapes real tools expect.
            if (!string.Equals(t.File, lastFile, StringComparison.Ordinal))
            {
                sb.Append("FILE \"").Append(t.File).Append("\" BINARY\n");
                lastFile = t.File;
            }
            sb.Append("  TRACK ").Append(t.Number.ToString("D2", CultureInfo.InvariantCulture))
              .Append(' ').Append(token).Append('\n');
            if (t.Title is not null) sb.Append("    TITLE \"").Append(t.Title).Append("\"\n");
            if (t.Performer is not null) sb.Append("    PERFORMER \"").Append(t.Performer).Append("\"\n");
            if (t.Flags != CueFlags.None)
            {
                sb.Append("    FLAGS");
                if (t.Flags.HasFlag(CueFlags.Dcp)) sb.Append(" DCP");
                if (t.Flags.HasFlag(CueFlags.FourChannel)) sb.Append(" 4CH");
                if (t.Flags.HasFlag(CueFlags.PreEmphasis)) sb.Append(" PRE");
                if (t.Flags.HasFlag(CueFlags.Scms)) sb.Append(" SCMS");
                sb.Append('\n');
            }
            if (t.Isrc is not null) sb.Append("    ISRC ").Append(t.Isrc).Append('\n');
            if (t.Pregap is { } pg)
                sb.Append("    PREGAP ").Append(pg).Append('\n');
            foreach (var idx in t.Indices)
                sb.Append("    INDEX ")
                  .Append(idx.Number.ToString("D2", CultureInfo.InvariantCulture))
                  .Append(' ').Append(idx.Time).Append('\n');
            if (t.Postgap is { } pog)
                sb.Append("    POSTGAP ").Append(pog).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// CUE parser: FILE / TRACK / PREGAP / POSTGAP / INDEX / FLAGS / ISRC /
    /// CATALOG, plus the CD-TEXT directives TITLE and PERFORMER at both disc
    /// and track scope, and "REM SESSION n" markers (Redump's multisession
    /// convention). Other unknown directives (CDTEXTFILE, SONGWRITER…) are ignored
    /// rather than rejected.
    /// </summary>
    public static CueSheet Parse(string text)
    {
        var tracks = new List<CueTrack>();
        string? catalog = null, discTitle = null, discPerformer = null;
        string? currentFile = null;   // most recent FILE directive seen
        string? trackFile = null;     // the FILE that owns the *pending* track
        int currentSession = 1;       // most recent "REM SESSION n" seen
        int trackSession = 1;         // the session that owns the *pending* track
        int? trackNo = null;
        CueTrackType type = default;
        Msf? pregap = null, postgap = null;
        CueFlags flags = CueFlags.None;
        string? isrc = null, title = null, performer = null;
        var indices = new List<CueIndex>();

        void Flush()
        {
            if (trackNo is { } n)
            {
                tracks.Add(new CueTrack
                {
                    Number = n, Type = type,
                    File = trackFile ?? throw new FormatException("TRACK before FILE."),
                    Pregap = pregap, Postgap = postgap, Indices = indices.ToList(),
                    Session = trackSession,
                    Flags = flags, Isrc = isrc, Title = title, Performer = performer,
                });
            }
            trackNo = null; pregap = null; postgap = null;
            flags = CueFlags.None; isrc = null; title = null; performer = null;
            indices = new List<CueIndex>();
        }

        static string Unquote(string s)
        {
            int q1 = s.IndexOf('"'), q2 = s.LastIndexOf('"');
            return (q1 >= 0 && q2 > q1) ? s[(q1 + 1)..q2] : s;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var sp = line.IndexOf(' ');
            var kw = (sp < 0 ? line : line[..sp]).ToUpperInvariant();
            var rest = sp < 0 ? "" : line[(sp + 1)..].Trim();

            switch (kw)
            {
                case "FILE":
                    // FILE "name" BINARY  — extract the quoted name.
                    int q1 = rest.IndexOf('"'), q2 = rest.LastIndexOf('"');
                    currentFile = (q1 >= 0 && q2 > q1) ? rest[(q1 + 1)..q2] : rest.Split(' ')[0];
                    break;
                case "TRACK":
                    // Flush the previous track FIRST (it keeps its own trackFile),
                    // then bind this track to the most recent FILE.
                    Flush();
                    trackFile = currentFile;
                    trackSession = currentSession;
                    var tp = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    trackNo = int.Parse(tp[0], CultureInfo.InvariantCulture);
                    type = TokenToType(tp[1]);
                    break;
                case "PREGAP":
                    pregap = Msf.Parse(rest);
                    break;
                case "POSTGAP":
                    postgap = Msf.Parse(rest);
                    break;
                case "INDEX":
                    var ip = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    indices.Add(new CueIndex(
                        int.Parse(ip[0], CultureInfo.InvariantCulture), Msf.Parse(ip[1])));
                    break;
                case "FLAGS":
                    foreach (var f in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        flags |= f.ToUpperInvariant() switch
                        {
                            "DCP" => CueFlags.Dcp,
                            "4CH" => CueFlags.FourChannel,
                            "PRE" => CueFlags.PreEmphasis,
                            "SCMS" => CueFlags.Scms,
                            _ => CueFlags.None,           // unknown flag: ignore
                        };
                    break;
                case "ISRC":
                    isrc = rest.Trim();
                    break;
                case "CATALOG":
                    catalog = rest.Trim();
                    break;
                case "TITLE":
                    if (trackNo is null) discTitle = Unquote(rest);
                    else title = Unquote(rest);
                    break;
                case "PERFORMER":
                    if (trackNo is null) discPerformer = Unquote(rest);
                    else performer = Unquote(rest);
                    break;
                case "REM":
                    // Redump marks each session with "REM SESSION n"; capture it so
                    // a multisession disc (e.g. a Dreamcast MIL-CD) can be rebuilt
                    // into the right number of sessions. Other REM comments are ignored.
                    var remParts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (remParts.Length >= 2
                        && remParts[0].Equals("SESSION", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(remParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sn)
                        && sn > 0)
                        currentSession = sn;
                    break;
                // Other ignored directives (SONGWRITER, CDTEXTFILE, …).
                default: break;
            }
        }
        Flush();
        return new CueSheet
        {
            Tracks = tracks, Catalog = catalog,
            Title = discTitle, Performer = discPerformer,
        };
    }
}
