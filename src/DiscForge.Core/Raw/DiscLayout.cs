// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cdi;
using DiscForge.Core.Cue;

namespace DiscForge.Core.Raw;

public enum RawTrackMode { Audio, Mode1, Mode2 }

/// <summary>An index point beyond index 1: number and sector offset from the
/// start of index 1.</summary>
public sealed record RawIndex(int Number, int OffsetSectors);

/// <summary>
/// One track of a disc to be written RAW, in purely logical terms: where its
/// bytes come from, how long each region is, and everything the sub-channel
/// must say about it. All sector counts are 1 sector = 1/75 s.
/// </summary>
public sealed record RawTrack
{
    public required int Number { get; init; }
    public required RawTrackMode Mode { get; init; }

    /// <summary>Q control bits (data / copy-permitted / pre-emphasis / 4ch).</summary>
    public QControl Control { get; init; }

    /// <summary>12-character ISRC, written into the track's Q (ADR 3) frames.</summary>
    public string? Isrc { get; init; }

    /// <summary>Pregap sectors whose content IS stored in the source
    /// (CUE INDEX 00 region; CDI stored pregap).</summary>
    public int PregapStoredSectors { get; init; }

    /// <summary>Pregap sectors to generate (CUE PREGAP directive; track 1's
    /// mandatory 150-sector minimum is topped up here).</summary>
    public int PregapGeneratedSectors { get; init; }

    /// <summary>Sectors from index 1 to the end of the track's stored data.</summary>
    public required int LengthSectors { get; init; }

    /// <summary>Silence appended after the stored data (CUE POSTGAP).</summary>
    public int PostgapSectors { get; init; }

    /// <summary>Index points beyond 1, ascending by offset.</summary>
    public IReadOnlyList<RawIndex> ExtraIndexes { get; init; } = Array.Empty<RawIndex>();

    // --- where the bytes live ----------------------------------------------

    /// <summary>Stream holding this track's stored sectors. Not owned.</summary>
    public required Stream Source { get; init; }
    /// <summary>Byte offset of the track's first STORED sector (including any
    /// stored pregap) within <see cref="Source"/>.</summary>
    public required long SourceByteOffset { get; init; }
    /// <summary>Stored size of each sector: 2048, 2336 or 2352.</summary>
    public required int StoredSectorSize { get; init; }

    /// <summary>
    /// Optional sub-channel sidecar (CD+G and friends): 96 bytes per stored
    /// sector, raw interleaved P-W as CloneCD-style .sub files carry it,
    /// aligned 1:1 with the stored sectors. Only the R–W symbols are used —
    /// P and Q are DiscForge's to write from the layout.
    /// </summary>
    public Stream? SubSource { get; init; }
    /// <summary>Byte offset of the first stored sector's 96-byte sub frame.</summary>
    public long SubByteOffset { get; init; }

    /// <summary>
    /// When true, <see cref="SubSource"/> is emitted VERBATIM — the whole
    /// 96-byte frame including P and Q, not just R–W. This is the faithful
    /// console-backup mode: it preserves deliberately-corrupt Q sub-channel
    /// (LibCrypt and similar) that DiscForge's own Q generation would "fix"
    /// and thereby break. Ignored unless <see cref="SubSource"/> is set.
    /// </summary>
    public bool SubVerbatim { get; init; }

    public int PregapTotalSectors => PregapStoredSectors + PregapGeneratedSectors;
    public int TotalSectors => PregapTotalSectors + LengthSectors + PostgapSectors;
}

/// <summary>
/// A whole single-session disc, ready for the raw image generator. Owns any
/// streams opened on its behalf (the CUE loader's data files).
/// </summary>
public sealed class DiscLayout : IDisposable
{
    public required IReadOnlyList<RawTrack> Tracks { get; init; }
    /// <summary>13-digit media catalog number (CUE CATALOG), if any.</summary>
    public string? Mcn { get; init; }
    public CdTextBuilder.DiscText CdText { get; init; } = new();

    private readonly List<IDisposable> _owned = new();
    internal void Own(IDisposable d) => _owned.Add(d);

    /// <summary>
    /// Closes every stream this layout opened. Safe to call more than once, and
    /// one stream that objects to being closed doesn't strand the rest — a
    /// half-released set of file handles is worse than a swallowed error here.
    /// </summary>
    public void Dispose()
    {
        foreach (var d in _owned)
        {
            try { d.Dispose(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
        _owned.Clear();
    }

    /// <summary>A0's PSEC disc type: 0x20 when any track is Mode 2 (XA), else 00.</summary>
    public byte DiscType => Tracks.Any(t => t.Mode == RawTrackMode.Mode2) ? (byte)0x20 : (byte)0x00;

    /// <summary>True when any track carries program-area R–W data (CD+G) —
    /// the burn then needs a 96-byte subcode sector type, not PQ-16.</summary>
    public bool HasProgramRw => Tracks.Any(t => t.SubSource is not null && !t.SubVerbatim);

    /// <summary>True when any track re-emits the source's sub-channel verbatim
    /// (protection preservation) — also needs a 96-byte sector type.</summary>
    public bool HasVerbatimSubchannel => Tracks.Any(t => t is { SubSource: not null, SubVerbatim: true });

    // ---- from CUE ----------------------------------------------------------

    /// <summary>
    /// Build a layout from a parsed CUE sheet. <paramref name="openFile"/>
    /// resolves each FILE name to a stream; streams are opened once per file
    /// and owned by the returned layout.
    ///
    /// If loading fails part-way — a mismatched .sub, a missing INDEX 01, a
    /// file that isn't a whole number of sectors — everything opened so far is
    /// closed before the exception leaves. The layout owns those handles but is
    /// never returned on that path, so without this the caller has no object to
    /// dispose and the data files stay locked for the life of the process. In a
    /// GUI session that means a .bin the user can't delete or overwrite, with
    /// nothing on screen connecting it to the CUE they rejected minutes ago.
    /// </summary>
    public static DiscLayout FromCue(CueSheet cue, Func<string, Stream> openFile,
                                     Func<string, Stream?>? openSub = null,
                                     bool subVerbatim = false)
    {
        if (cue.Tracks.Count == 0)
            throw new InvalidDataException("The CUE sheet has no tracks.");

        var layout = new DiscLayout
        {
            Tracks = new List<RawTrack>(),
            Mcn = cue.Catalog,
            CdText = new CdTextBuilder.DiscText
            {
                AlbumTitle = cue.Title,
                AlbumPerformer = cue.Performer,
                Tracks = cue.Tracks
                    .Select(t => new CdTextBuilder.TrackText(t.Title, t.Performer)).ToList(),
            },
        };

        try
        {
            Build(cue, layout, openFile, openSub, subVerbatim);
            return layout;
        }
        catch
        {
            layout.Dispose();
            throw;
        }
    }

    private static void Build(CueSheet cue, DiscLayout layout, Func<string, Stream> openFile,
                              Func<string, Stream?>? openSub, bool subVerbatim)
    {
        var streams = new Dictionary<string, Stream>(StringComparer.OrdinalIgnoreCase);
        Stream Open(string name)
        {
            if (!streams.TryGetValue(name, out var s))
            {
                s = openFile(name);
                streams[name] = s;
                layout.Own(s);
            }
            return s;
        }

        // Sub-channel sidecars, one per FILE, validated against the file's
        // sector count so a mismatched .sub fails loudly at load time rather
        // than as garbage graphics on a disc.
        var subs = new Dictionary<string, Stream?>(StringComparer.OrdinalIgnoreCase);
        Stream? OpenSub(string name, long fileSectors)
        {
            if (!subs.TryGetValue(name, out var s))
            {
                s = openSub?.Invoke(name);
                if (s is not null)
                {
                    if (s.Length != fileSectors * 96)
                    {
                        long got = s.Length;
                        s.Dispose();
                        throw new InvalidDataException(
                            $"The sub-channel sidecar for '{name}' is {got:N0} bytes but " +
                            $"{fileSectors:N0} sectors need {fileSectors * 96:N0} " +
                            "(96 bytes per sector). Wrong file, or a different dump format.");
                    }
                    layout.Own(s);
                }
                subs[name] = s;
            }
            return s;
        }

        var tracks = (List<RawTrack>)layout.Tracks;
        for (int i = 0; i < cue.Tracks.Count; i++)
        {
            var t = cue.Tracks[i];
            var stream = Open(t.File);
            int stored = CueSheet.TypeToToken(t.Type).sectorSize;

            var index1 = t.Indices.FirstOrDefault(x => x.Number == 1)
                ?? throw new InvalidDataException($"Track {t.Number} has no INDEX 01.");
            var index0 = t.Indices.FirstOrDefault(x => x.Number == 0);

            // The track's stored region starts at its earliest index and runs
            // to the next track's earliest index in the SAME file, or to EOF.
            long regionStart = (index0 ?? index1).Time.ToSectors();
            long regionEnd;
            var next = cue.Tracks.Skip(i + 1)
                .FirstOrDefault(n => string.Equals(n.File, t.File, StringComparison.OrdinalIgnoreCase));
            if (next is not null)
            {
                var nFirst = next.Indices.MinBy(x => x.Number)
                    ?? throw new InvalidDataException($"Track {next.Number} has no indexes.");
                regionEnd = nFirst.Time.ToSectors();
            }
            else
            {
                long bytes = stream.Length - regionStart * stored;
                if (bytes < 0 || bytes % stored != 0)
                    throw new InvalidDataException(
                        $"'{t.File}' is not a whole number of {stored}-byte sectors after " +
                        $"track {t.Number}'s start.");
                regionEnd = regionStart + bytes / stored;
            }

            int pregapStored = (int)(index1.Time.ToSectors() - regionStart);
            int length = (int)(regionEnd - index1.Time.ToSectors());
            if (length <= 0)
                throw new InvalidDataException($"Track {t.Number} has no sectors after INDEX 01.");

            int pregapGenerated = (int)(t.Pregap?.ToSectors() ?? 0);
            if (i == 0 && pregapStored + pregapGenerated < 150)
                pregapGenerated = 150 - pregapStored;      // Red Book minimum

            var extra = t.Indices.Where(x => x.Number > 1)
                .OrderBy(x => x.Number)
                .Select(x => new RawIndex(x.Number,
                    (int)(x.Time.ToSectors() - index1.Time.ToSectors())))
                .ToList();
            if (extra.Any(x => x.OffsetSectors < 0 || x.OffsetSectors >= length))
                throw new InvalidDataException($"Track {t.Number} has an index outside the track.");

            var mode = t.Type switch
            {
                CueTrackType.Audio => RawTrackMode.Audio,
                CueTrackType.Mode1_2048 or CueTrackType.Mode1_2352 => RawTrackMode.Mode1,
                _ => RawTrackMode.Mode2,
            };

            var control = QControl.None;
            if (mode != RawTrackMode.Audio) control |= QControl.Data;
            if (t.Flags.HasFlag(CueFlags.Dcp)) control |= QControl.CopyPermitted;
            if (mode == RawTrackMode.Audio && t.Flags.HasFlag(CueFlags.PreEmphasis))
                control |= QControl.PreEmphasis;
            if (mode == RawTrackMode.Audio && t.Flags.HasFlag(CueFlags.FourChannel))
                control |= QControl.FourChannel;

            var sub = OpenSub(t.File, stream.Length / stored);
            tracks.Add(new RawTrack
            {
                Number = t.Number,
                Mode = mode,
                Control = control,
                Isrc = t.Isrc,
                PregapStoredSectors = pregapStored,
                PregapGeneratedSectors = pregapGenerated,
                LengthSectors = length,
                PostgapSectors = (int)(t.Postgap?.ToSectors() ?? 0),
                ExtraIndexes = extra,
                Source = stream,
                SourceByteOffset = regionStart * stored,
                StoredSectorSize = stored,
                SubSource = sub,
                SubByteOffset = regionStart * 96,
                SubVerbatim = subVerbatim && sub is not null,
            });
        }
    }

    /// <summary>Convenience: load a .cue from disk, opening its data files
    /// relative to the sheet's directory.</summary>
    public static DiscLayout FromCueFile(string cuePath, bool subVerbatim = false)
    {
        var cue = CueSheet.Parse(File.ReadAllText(cuePath));
        var dir = Path.GetDirectoryName(Path.GetFullPath(cuePath))!;
        string Resolve(string name) => Path.IsPathRooted(name) ? name : Path.Combine(dir, name);
        return FromCue(cue,
            name => File.OpenRead(Resolve(name)),
            name =>
            {
                // CloneCD convention first (album.bin -> album.sub), then the
                // literal append (album.bin.sub).
                var swapped = Path.ChangeExtension(Resolve(name), ".sub");
                if (File.Exists(swapped)) return File.OpenRead(swapped);
                var appended = Resolve(name) + ".sub";
                return File.Exists(appended) ? File.OpenRead(appended) : null;
            },
            subVerbatim);
    }

    // ---- from CDI ----------------------------------------------------------

    /// <summary>
    /// Build a layout from a parsed CDI image. Single-session only — RAW DAO
    /// writes one session; multisession CDI is refused by the planner before
    /// reaching this point, but the check here keeps the API honest.
    /// </summary>
    public static DiscLayout FromCdi(CdiImage image, Stream cdi)
    {
        if (image.Sessions.Count > 1)
            throw new NotSupportedException(
                "RAW DAO writes a single session; this CDI image has " +
                $"{image.Sessions.Count}. Multisession raw writing is the SPTI engine's job.");

        var tracks = new List<RawTrack>();
        foreach (var t in image.AllTracks)
        {
            var mode = t.Mode switch
            {
                CdiTrackMode.Audio => RawTrackMode.Audio,
                CdiTrackMode.Mode1 => RawTrackMode.Mode1,
                _ => RawTrackMode.Mode2,
            };
            tracks.Add(new RawTrack
            {
                Number = t.Number,
                Mode = mode,
                Control = mode == RawTrackMode.Audio ? QControl.None : QControl.Data,
                PregapStoredSectors = (int)t.PregapSectors,
                PregapGeneratedSectors =
                    t.Number == image.AllTracks.First().Number && t.PregapSectors < 150
                        ? 150 - (int)t.PregapSectors : 0,
                LengthSectors = (int)t.LengthSectors,
                Source = cdi,
                SourceByteOffset = t.FileOffset,
                StoredSectorSize = (int)t.SectorSize,
            });
        }
        return new DiscLayout { Tracks = tracks };
    }
}