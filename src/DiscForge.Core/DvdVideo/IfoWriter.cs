// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.DvdVideo;

/// <summary>
/// Writes the DVD-Video IFO structure — <c>VIDEO_TS.IFO</c> (the video manager,
/// VMG) and one <c>VTS_nn_0.IFO</c> per title set — from a structural plan. It is
/// the other half of <see cref="IfoReader"/>: what the reader enumerates, this
/// emits, so a plan written here reads back identically (the round-trip that
/// validates both halves, exactly as UDF, XISO and NRG are validated).
///
/// This composes the parts the format needs to describe a disc's *structure*:
/// the VMGI_MAT header with the title-set count and the TT_SRPT title table
/// (title → title-set mapping, chapter and angle counts), and each VTSI_MAT with
/// its TITLE-domain audio and subpicture stream attributes. Size and pointer
/// fields are filled coherently (last-sector values, the TT_SRPT pointer), so the
/// output is a real IFO shape, not just the handful of bytes the reader happens to
/// look at.
///
/// Scope — honest: this emits the structural IFO (enumeration, streams, budgeting map) AND the
/// navigation tables a player walks — VTS_PGCIT (one program chain per title, with its program
/// count = chapters, one cell per program, and the playback duration), VTS_C_ADT (a cell-address
/// entry per cell) and VTS_VOBU_ADMAP — with coherent pointers, so the reader parses the program
/// chains back (the round-trip that validates both halves). What it does NOT invent is the real
/// per-VOBU/cell *sector addresses*: those only exist once video is muxed, so they are left zero
/// and remain the job of the <c>dvdauthor</c> runner. The result is a native, dependency-free
/// writer whose navigation structure (chains, programs, cells, durations) is complete and
/// round-trippable, with only the mux-time sector addresses deferred.
///
/// Nothing here decodes, encodes or decrypts video; IFO files are unencrypted
/// even on a CSS disc, so this stays within the clean-room boundary.
/// </summary>
public static class IfoWriter
{
    public const int SectorSize = 2048;

    // ---- the plan the caller supplies --------------------------------------

    public sealed record AudioPlan
    {
        /// <summary>AC3, MPEG1, MPEG2, LPCM or DTS.</summary>
        public required string Codec { get; init; }
        public int Channels { get; init; } = 2;
        /// <summary>ISO 639 two-letter code, or empty/blank for "not specified".</summary>
        public string Language { get; init; } = "";
    }

    public sealed record SubtitlePlan
    {
        public string Language { get; init; } = "";
    }

    public sealed record TitlePlan
    {
        public int Chapters { get; init; } = 1;
        public int Angles { get; init; } = 1;
        /// <summary>Total playback time in whole seconds, written into the PGC's playback time
        /// (0 = not specified). Real per-VOBU addresses still come from muxing; this is the
        /// navigation-structure duration a player reads from the program chain.</summary>
        public int DurationSeconds { get; init; } = 0;
    }

    public sealed record TitleSetPlan
    {
        public required int Number { get; init; }            // VTS_nn
        public IReadOnlyList<TitlePlan> Titles { get; init; } = Array.Empty<TitlePlan>();
        public IReadOnlyList<AudioPlan> Audio { get; init; } = Array.Empty<AudioPlan>();
        public IReadOnlyList<SubtitlePlan> Subtitles { get; init; } = Array.Empty<SubtitlePlan>();
    }

    public sealed record DvdPlan
    {
        public required IReadOnlyList<TitleSetPlan> TitleSets { get; init; }
    }

    // ---- entry point --------------------------------------------------------

    /// <summary>Emit every IFO file for the plan, keyed by its VIDEO_TS name
    /// ("VIDEO_TS.IFO", "VTS_01_0.IFO", …). Deterministic: the same plan yields
    /// byte-identical output.</summary>
    public static IReadOnlyDictionary<string, byte[]> Write(DvdPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.TitleSets.Count == 0)
            throw new ArgumentException("A DVD needs at least one title set.", nameof(plan));

        var seen = new HashSet<int>();
        foreach (var s in plan.TitleSets)
        {
            if (s.Number is < 1 or > 99)
                throw new ArgumentException($"Title-set number {s.Number} is out of range (1–99).");
            if (!seen.Add(s.Number))
                throw new ArgumentException($"Duplicate title-set number {s.Number}.");
        }

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["VIDEO_TS.IFO"] = BuildVmg(plan),
        };
        foreach (var set in plan.TitleSets)
            files[$"VTS_{set.Number:00}_0.IFO"] = BuildVts(set);
        return files;
    }

    /// <summary>Turn a structure just read by <see cref="IfoReader"/> back into a
    /// writable plan — the basis of a structural rewrite: read a disc, drop or keep
    /// title sets, and re-emit its IFOs. Passing the whole structure through
    /// unchanged reproduces the enumeration (read → write → read is stable).</summary>
    public static DvdPlan PlanFrom(IfoReader.DvdStructure structure)
    {
        ArgumentNullException.ThrowIfNull(structure);
        var sets = new List<TitleSetPlan>(structure.TitleSets.Count);
        foreach (var set in structure.TitleSets)
        {
            var first = set.Titles.FirstOrDefault();
            sets.Add(new TitleSetPlan
            {
                Number = set.Number,
                Titles = set.Titles
                    .Select(t => new TitlePlan { Chapters = t.Chapters, Angles = t.AngleCount })
                    .ToList(),
                Audio = (first?.Audio ?? Array.Empty<IfoReader.AudioStream>())
                    .Select(a => new AudioPlan { Codec = a.Codec, Channels = a.Channels, Language = a.Language })
                    .ToList(),
                Subtitles = (first?.Subtitles ?? Array.Empty<IfoReader.SubtitleStream>())
                    .Select(s => new SubtitlePlan { Language = s.Language })
                    .ToList(),
            });
        }
        return new DvdPlan { TitleSets = sets };
    }

    /// <summary>A structural rewrite keeping only the named title sets (by VTS
    /// number). Renumbers the survivors 1..n so the emitted disc is contiguous —
    /// the reauthor "keep a subset" operation at the structural level.</summary>
    public static DvdPlan Keep(IfoReader.DvdStructure structure, IEnumerable<int> titleSetNumbers)
    {
        ArgumentNullException.ThrowIfNull(structure);
        var keep = new HashSet<int>(titleSetNumbers);
        var plan = PlanFrom(structure);
        var kept = plan.TitleSets.Where(s => keep.Contains(s.Number)).ToList();
        if (kept.Count == 0)
            throw new ArgumentException("The selection keeps no title set.");
        var renumbered = kept
            .Select((s, i) => s with { Number = i + 1 })
            .ToList();
        return new DvdPlan { TitleSets = renumbered };
    }

    // ---- VMG (VIDEO_TS.IFO) -------------------------------------------------

    private static byte[] BuildVmg(DvdPlan plan)
    {
        // Sector 0: VMGI_MAT header. Sector 1: TT_SRPT (title table). Two sectors
        // hold up to ~170 titles — beyond a real disc's 99-title limit.
        int titleCount = plan.TitleSets.Sum(s => s.Titles.Count);
        if (titleCount > 99)
            throw new ArgumentException($"A DVD holds at most 99 titles; the plan has {titleCount}.");

        const int sectors = 2;
        var ifo = new byte[sectors * SectorSize];

        Encoding.ASCII.GetBytes("DVDVIDEO-VMG").CopyTo(ifo, 0);
        // 0x0C last sector of the whole VMG (IFO + BUP, no menu VOB) = 2N-1.
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(0x0C), (uint)(sectors * 2 - 1));
        // 0x1C last sector of the VMGI (the IFO alone) = N-1.
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(0x1C), (uint)(sectors - 1));
        // 0x20 version — DVD-Video 1.1 (0x0011).
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(0x20), 0x0011);
        // 0x3E number of title sets.
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(0x3E), (ushort)plan.TitleSets.Count);
        // 0xC4 TT_SRPT start sector (relative to the IFO) — sector 1.
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(0xC4), 1);

        // TT_SRPT at sector 1: count(2) + reserved(2) + end-address(4), then a
        // 12-byte entry per title, in disc-global order.
        int table = SectorSize;
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(table), (ushort)titleCount);
        int tableBytes = 8 + titleCount * 12;
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(table + 4), (uint)(tableBytes - 1));

        int idx = 0;
        foreach (var set in plan.TitleSets)
        {
            for (int vtsTitle = 1; vtsTitle <= set.Titles.Count; vtsTitle++)
            {
                var t = set.Titles[vtsTitle - 1];
                int at = table + 8 + idx * 12;
                ifo[at] = 0x00;                                          // playback type
                ifo[at + 1] = (byte)(Math.Clamp(t.Angles, 1, 9) & 0x0F); // angle count (low nibble)
                BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(at + 2), (ushort)Math.Max(1, t.Chapters));
                ifo[at + 6] = (byte)set.Number;                          // title-set number
                ifo[at + 7] = (byte)vtsTitle;                            // title index within the VTS
                // [8..11] VTS starting sector — no VOB layout at the structural
                // level, so left zero.
                idx++;
            }
        }

        return ifo;
    }

    // ---- VTS (VTS_nn_0.IFO) -------------------------------------------------

    private static byte[] BuildVts(TitleSetPlan set)
    {
        // Sector 0: VTSI_MAT + stream attributes. Then VTS_PGCIT (program chains), VTS_C_ADT (cell
        // address table) and VTS_VOBU_ADMAP, each placed at a sector boundary — the PGCIT and C_ADT
        // may span several sectors when a title has many chapters/cells, exactly as a real disc.
        int totalCells = set.Titles.Sum(t => Math.Max(1, t.Chapters));
        byte[] pgcit = BuildPgcit(set);
        byte[] cadt = BuildCadt(totalCells);
        byte[] vobu = BuildVobuAdmap();

        int pgcitSector = 1;
        int cadtSector = pgcitSector + Sectors(pgcit.Length);
        int vobuSector = cadtSector + Sectors(cadt.Length);
        int sectors = vobuSector + Sectors(vobu.Length);
        var ifo = new byte[sectors * SectorSize];

        Encoding.ASCII.GetBytes("DVDVIDEO-VTS").CopyTo(ifo, 0);
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(0x0C), (uint)(sectors * 2 - 1)); // whole VTS last sector
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(0x1C), (uint)(sectors - 1));     // VTSI last sector
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(0x20), 0x0011);                  // version

        // TITLE-domain audio: count is a 16-bit big-endian value at 0x202 (so the
        // low byte lands at 0x203, where the reader looks), attributes 8 bytes each
        // from 0x204. The format caps this at 8.
        var audio = set.Audio;
        if (audio.Count > 8)
            throw new ArgumentException($"A title set holds at most 8 audio streams; VTS {set.Number} has {audio.Count}.");
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(0x202), (ushort)audio.Count);
        for (int i = 0; i < audio.Count; i++)
            EncodeAudio(ifo.AsSpan(0x204 + i * 8, 8), audio[i]);

        // TITLE-domain subpictures: count (16-bit BE) at 0x254 (low byte at 0x255),
        // attributes 6 bytes each from 0x256. The format caps this at 32.
        var subs = set.Subtitles;
        if (subs.Count > 32)
            throw new ArgumentException($"A title set holds at most 32 subpicture streams; VTS {set.Number} has {subs.Count}.");
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(0x254), (ushort)subs.Count);
        for (int i = 0; i < subs.Count; i++)
            EncodeSubtitle(ifo.AsSpan(0x256 + i * 6, 6), subs[i]);

        // ---- navigation tables: one PGC per title -------------------------------
        // VTSI_MAT sector pointers (BE, relative to the VTS IFO): VTS_PGCIT @0xCC,
        // VTS_C_ADT @0xE0, VTS_VOBU_ADMAP @0xE4.
        pgcit.CopyTo(ifo, pgcitSector * SectorSize);
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(0xCC), (uint)pgcitSector);
        cadt.CopyTo(ifo, cadtSector * SectorSize);
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(0xE0), (uint)cadtSector);
        vobu.CopyTo(ifo, vobuSector * SectorSize);
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(0xE4), (uint)vobuSector);

        return ifo;
    }

    // VTS_PGCIT: nr_of_pgci_srp(2) + reserved(2) + last_byte(4), then an 8-byte PGCI_SRP per PGC
    // (entry_id, reserved, ptl_mask, pgc_start_byte), then the PGCs. One PGC per title.
    private static byte[] BuildPgcit(TitleSetPlan set)
    {
        int nr = Math.Max(1, set.Titles.Count);
        var pgcs = new List<byte[]>();
        for (int i = 0; i < nr; i++)
        {
            var t = i < set.Titles.Count ? set.Titles[i] : new TitlePlan();
            pgcs.Add(BuildPgc(Math.Max(1, t.Chapters), t.DurationSeconds));
        }

        int srpTable = 8 + nr * 8;
        int total = srpTable + pgcs.Sum(p => p.Length);
        var table = new byte[total];

        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(0x00), (ushort)nr);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(0x04), (uint)(total - 1));   // last_byte

        int pgcOffset = srpTable;
        for (int i = 0; i < nr; i++)
        {
            int srp = 8 + i * 8;
            table[srp] = (byte)(0x80 | ((i + 1) & 0x7F));                                // entry PGC, title number
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(srp + 4), (uint)pgcOffset);
            pgcs[i].CopyTo(table, pgcOffset);
            pgcOffset += pgcs[i].Length;
        }
        return table;
    }

    // A PGC: 0xEC-byte header, then a (empty) command table, program map, cell playback table and
    // cell position table. Programs = chapters, one cell per program. Real VOBU sector addresses
    // come from muxing and stay zero here; the counts and the playback duration are the navigation
    // structure a player reads.
    private static byte[] BuildPgc(int programs, int durationSeconds)
    {
        // nr_of_programs and nr_of_cells are 8-bit fields — the format caps a PGC at 255 (real
        // discs at 99). Clamp so the emitted PGC is always well-formed even if a caller's chapter
        // count (a 16-bit TT_SRPT value) is larger.
        programs = Math.Clamp(programs, 1, 255);
        int cells = programs;
        int header = 0xEC;
        int cmdTable = 8;                                   // empty: nr_pre/post/cell = 0
        int programMap = (programs + 1) & ~1;               // 1 byte/program, padded to even
        int cellPlayback = cells * 24;
        int cellPosition = cells * 4;

        int cmdOff = header;
        int mapOff = cmdOff + cmdTable;
        int playOff = mapOff + programMap;
        int posOff = playOff + cellPlayback;
        int total = posOff + cellPosition;

        var pgc = new byte[total];
        pgc[0x02] = (byte)programs;
        pgc[0x03] = (byte)cells;
        EncodeBcdTime(pgc.AsSpan(0x04, 4), durationSeconds);
        BinaryPrimitives.WriteUInt16BigEndian(pgc.AsSpan(0xE4), (ushort)cmdOff);
        BinaryPrimitives.WriteUInt16BigEndian(pgc.AsSpan(0xE6), (ushort)mapOff);
        BinaryPrimitives.WriteUInt16BigEndian(pgc.AsSpan(0xE8), (ushort)playOff);
        BinaryPrimitives.WriteUInt16BigEndian(pgc.AsSpan(0xEA), (ushort)posOff);

        // Empty command table: pre/post/cell counts 0, last_byte points at the header end.
        BinaryPrimitives.WriteUInt16BigEndian(pgc.AsSpan(cmdOff + 6), (ushort)(cmdTable - 1));

        // Program map: program i starts at cell i+1 (1-based).
        for (int i = 0; i < programs; i++) pgc[mapOff + i] = (byte)(i + 1);

        // Cell playback (24 B each): per-cell playback time left zero; the PGC total is authoritative.
        // Cell position (4 B each): vob_id = 1, cell_id = i+1.
        for (int i = 0; i < cells; i++)
        {
            int pos = posOff + i * 4;
            BinaryPrimitives.WriteUInt16BigEndian(pgc.AsSpan(pos), 1);   // vob_id
            pgc[pos + 3] = (byte)(i + 1);                                // cell_id
        }
        return pgc;
    }

    // VTS_C_ADT: nr_of_vobs(2) + reserved(2) + last_byte(4), then a 12-byte cell-address entry per
    // cell. Sector addresses come from muxing and stay zero at the structural level.
    private static byte[] BuildCadt(int cells)
    {
        int total = 8 + cells * 12;
        var t = new byte[Math.Max(total, 8)];
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(0x00), (ushort)Math.Max(1, cells > 0 ? 1 : 0)); // nr_of_vobs
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(0x04), (uint)(total - 1));
        for (int i = 0; i < cells; i++)
        {
            int e = 8 + i * 12;
            BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(e), 1);   // vob_id
            t[e + 2] = (byte)(i + 1);                                 // cell_id
        }
        return t;
    }

    // VTS_VOBU_ADMAP: last_byte(4), then a u32 VOBU start sector each. One VOBU at sector 0 keeps
    // the table well-formed without inventing a real layout.
    private static byte[] BuildVobuAdmap()
    {
        var t = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(0x00), 7);   // last_byte: header + one entry
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(0x04), 0);   // VOBU 0 at sector 0
        return t;
    }

    private static int Sectors(int bytes) => (bytes + SectorSize - 1) / SectorSize;

    // DVD PGC playback time: BCD hour, minute, second, and a frame byte whose top two bits are the
    // frame rate (0b11 = 30 fps). Frames left 0 — the structural duration is whole seconds.
    private static void EncodeBcdTime(Span<byte> t, int seconds)
    {
        if (seconds < 0) seconds = 0;
        int h = seconds / 3600, m = (seconds / 60) % 60, s = seconds % 60;
        static byte Bcd(int v) => (byte)(((v / 10) << 4) | (v % 10));
        t[0] = Bcd(Math.Min(h, 99));
        t[1] = Bcd(m);
        t[2] = Bcd(s);
        t[3] = 0xC0;   // frame rate = 0b11 (30 fps), frame count 0
    }

    // Audio attribute (mirrors IfoReader.ParseVtsStreams):
    //   byte0: coding mode in bits 7..5; language-present flag == 1 in bits 3..2.
    //   byte1: channel count − 1 in bits 2..0.
    //   byte2,3: ISO 639 language (when present).
    private static void EncodeAudio(Span<byte> e, AudioPlan a)
    {
        int codingMode = a.Codec.ToUpperInvariant() switch
        {
            "AC3" => 0,
            "MPEG1" => 2,
            "MPEG2" => 3,
            "LPCM" => 4,
            "DTS" => 6,
            _ => throw new ArgumentException($"Unknown audio codec '{a.Codec}'."),
        };
        byte b0 = (byte)(codingMode << 5);
        string lang = (a.Language ?? "").Trim();
        if (lang.Length == 2)
        {
            b0 |= 0x04;                       // language-present bits == 1
            e[2] = (byte)lang[0];
            e[3] = (byte)lang[1];
        }
        e[0] = b0;
        e[1] = (byte)((Math.Clamp(a.Channels, 1, 8) - 1) & 0x07);
    }

    // Subpicture attribute (mirrors the reader): language present → top bit of
    // byte0 set, language ASCII at bytes 2,3.
    private static void EncodeSubtitle(Span<byte> e, SubtitlePlan s)
    {
        string lang = (s.Language ?? "").Trim();
        if (lang.Length == 2)
        {
            e[0] = 0x01;   // subp_attr type == 1 (language present) in the low two bits — matches IfoReader
            e[2] = (byte)lang[0];
            e[3] = (byte)lang[1];
        }
    }
}
