// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;

namespace DiscForge.Core.DvdVideo;

/// <summary>
/// A focused reader for the DVD-Video structure — the <c>VIDEO_TS</c> folder's
/// IFO files — extracting the map a reauthor/shrink workflow needs: the video
/// manager (VMG), each video title set (VTS), the titles within them, and the
/// audio and subtitle streams each title carries.
///
/// This parses the public, long-documented DVD-Video .IFO layout. It reads
/// structure only — it does not decode, decrypt, or touch the MPEG video in the
/// VOBs. On a CSS-encrypted disc the IFO files themselves are not encrypted, so
/// the structure is readable; DiscForge still refuses to handle the encrypted
/// VOB payload, keeping the clean-room boundary intact. The output feeds the
/// reauthor selection and the <see cref="BitBudget"/> planner.
///
/// Only the fields needed for enumeration and budgeting are decoded; the format
/// is large and most of it (navigation commands, cell playback, colour maps) is
/// irrelevant to sizing and stream selection.
/// </summary>
/// <summary>Presentation helpers for DVD stream descriptions — ISO 639-1 language names and
/// channel layouts, so reports read in plain language instead of raw codes.</summary>
internal static class DvdLanguage
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English", ["ja"] = "Japanese", ["fr"] = "French", ["de"] = "German",
        ["es"] = "Spanish", ["it"] = "Italian", ["pt"] = "Portuguese", ["nl"] = "Dutch",
        ["ru"] = "Russian", ["zh"] = "Chinese", ["ko"] = "Korean", ["sv"] = "Swedish",
        ["no"] = "Norwegian", ["da"] = "Danish", ["fi"] = "Finnish", ["pl"] = "Polish",
        ["cs"] = "Czech", ["hu"] = "Hungarian", ["el"] = "Greek", ["tr"] = "Turkish",
        ["ar"] = "Arabic", ["he"] = "Hebrew", ["th"] = "Thai", ["hi"] = "Hindi",
    };

    /// <summary>A friendly language name from a 2-letter code, or "undetermined" when unset.</summary>
    public static string Name(string? code)
    {
        code = code?.Trim();
        if (string.IsNullOrEmpty(code)) return "undetermined";
        return Names.TryGetValue(code, out var name) ? name : code.ToUpperInvariant();
    }

    /// <summary>A speaker layout from a channel count (6 -> "5.1").</summary>
    public static string ChannelLayout(int channels) => channels switch
    {
        <= 0 => "unknown",
        1 => "mono",
        2 => "stereo",
        6 => "5.1",
        7 => "6.1",
        8 => "7.1",
        _ => $"{channels}ch",
    };
}

public static class IfoReader
{
    public sealed record AudioStream
    {
        public required int Index { get; init; }
        public required string Codec { get; init; }   // AC3, DTS, LPCM, MPEG
        public required string Language { get; init; } // ISO 639 2-letter, or "  "
        public int Channels { get; init; }

        /// <summary>A human-readable one-liner for reports (not the record's default dump).</summary>
        public string Describe() =>
            $"Stream {Index}: {Codec}, {DvdLanguage.Name(Language)}, {DvdLanguage.ChannelLayout(Channels)}";
    }

    public sealed record SubtitleStream
    {
        public required int Index { get; init; }
        public required string Language { get; init; }

        /// <summary>A human-readable one-liner for reports (not the record's default dump).</summary>
        public string Describe() => $"Subtitle {Index}: {DvdLanguage.Name(Language)}";
    }

    public sealed record Title
    {
        public required int TitleNumber { get; init; }     // 1-based, disc-global
        public required int TitleSet { get; init; }        // which VTS
        public required int VtsTitle { get; init; }        // index within the VTS
        public required int Chapters { get; init; }
        public required int AngleCount { get; init; }
        public IReadOnlyList<AudioStream> Audio { get; init; } = Array.Empty<AudioStream>();
        public IReadOnlyList<SubtitleStream> Subtitles { get; init; } = Array.Empty<SubtitleStream>();
    }

    /// <summary>One program chain (PGC) from a title set's VTS_PGCIT — the navigation
    /// structure a player walks: how many programs (chapters) and cells it has, and its
    /// total playback time.</summary>
    public sealed record ProgramChain
    {
        public required int Programs { get; init; }
        public required int Cells { get; init; }
        /// <summary>Total playback time in whole seconds (decoded from the BCD PGC time).</summary>
        public required int DurationSeconds { get; init; }
    }

    public sealed record TitleSet
    {
        public required int Number { get; init; }          // VTS_nn
        public required long MenuVobBytes { get; init; }   // VTS_nn_0.VOB
        public required long TitleVobBytes { get; init; }  // VTS_nn_1.VOB … n.VOB summed
        public IReadOnlyList<Title> Titles { get; init; } = Array.Empty<Title>();
        /// <summary>The title-domain program chains parsed from VTS_PGCIT (empty if absent).</summary>
        public IReadOnlyList<ProgramChain> ProgramChains { get; init; } = Array.Empty<ProgramChain>();
    }

    public sealed record DvdStructure
    {
        public required IReadOnlyList<TitleSet> TitleSets { get; init; }
        public required IReadOnlyList<Title> Titles { get; init; }   // flat, disc-global order
        public long MenuVobBytes { get; init; }    // VIDEO_TS.VOB (VMG menu)
        public long TotalVideoBytes => TitleSets.Sum(s => s.TitleVobBytes);
        public long TotalMenuBytes => MenuVobBytes + TitleSets.Sum(s => s.MenuVobBytes);

        public string Summary =>
            $"{TitleSets.Count} title set(s), {Titles.Count} title(s); " +
            $"video {TotalVideoBytes:N0} B, menus {TotalMenuBytes:N0} B.";
    }

    /// <summary>Abstraction over where the IFO/VOB bytes come from (a folder on
    /// disk, or a UDF/ISO view of an image). Sizes are needed for budgeting.</summary>
    public interface IVideoTsSource
    {
        /// <summary>Read a VIDEO_TS file whole (IFOs are small). Null if absent.</summary>
        byte[]? ReadFile(string name);
        /// <summary>Byte length of a file, or 0 if absent (used for VOB sizes).</summary>
        long FileSize(string name);
    }

    /// <summary>Parse a VIDEO_TS structure from any source.</summary>
    public static DvdStructure Read(IVideoTsSource src)
    {
        var vmg = src.ReadFile("VIDEO_TS.IFO")
            ?? throw new IfoFormatException("VIDEO_TS.IFO not found — not a DVD-Video volume.");
        if (vmg.Length < 0x100 || !HasMagic(vmg, "DVDVIDEO-VMG"))
            throw new IfoFormatException("VIDEO_TS.IFO is not a valid VMG.");

        // VMG: number of title sets at 0x3E (16-bit BE). Title→VTS mapping lives
        // in the TT_SRPT table, whose sector is a 32-bit BE value at 0xC4.
        int vtsCount = BinaryPrimitives.ReadUInt16BigEndian(vmg.AsSpan(0x3E));
        var titles = ParseTtSrpt(vmg);

        long vmgMenu = src.FileSize("VIDEO_TS.VOB");

        var sets = new List<TitleSet>(vtsCount);
        var flatTitles = new List<Title>();
        int globalTitle = 0;

        for (int v = 1; v <= vtsCount; v++)
        {
            string ifoName = $"VTS_{v:00}_0.IFO";
            var vts = src.ReadFile(ifoName);
            if (vts is null || !HasMagic(vts, "DVDVIDEO-VTS"))
                continue;   // gap in numbering; skip

            long menuBytes = src.FileSize($"VTS_{v:00}_0.VOB");
            long titleBytes = 0;
            for (int part = 1; part <= 9; part++)
            {
                long sz = src.FileSize($"VTS_{v:00}_{part}.VOB");
                if (sz == 0) break;
                titleBytes += sz;
            }

            var (audio, subs) = ParseVtsStreams(vts);

            // Titles that belong to this VTS (from the global TT_SRPT).
            var vtsTitles = new List<Title>();
            foreach (var t in titles.Where(t => t.TitleSet == v))
            {
                var full = t with
                {
                    TitleNumber = ++globalTitle,
                    Audio = audio,
                    Subtitles = subs,
                };
                vtsTitles.Add(full);
                flatTitles.Add(full);
            }

            sets.Add(new TitleSet
            {
                Number = v,
                MenuVobBytes = menuBytes,
                TitleVobBytes = titleBytes,
                Titles = vtsTitles,
                ProgramChains = ParseVtsPgcit(vts),
            });
        }

        return new DvdStructure
        {
            TitleSets = sets,
            Titles = flatTitles,
            MenuVobBytes = vmgMenu,
        };
    }

    // TT_SRPT: the title table in the VMG. Pointer (sector) at 0xC4; the table
    // has a 2-byte count, then 12 bytes per title entry.
    private static List<Title> ParseTtSrpt(byte[] vmg)
    {
        uint sector = BinaryPrimitives.ReadUInt32BigEndian(vmg.AsSpan(0xC4));
        int off = (int)sector * 2048;
        var list = new List<Title>();
        if (off + 8 > vmg.Length) return list;

        int count = BinaryPrimitives.ReadUInt16BigEndian(vmg.AsSpan(off));
        int p = off + 8;   // skip count(2) + reserved(2) + end-address(4)
        for (int i = 0; i < count && p + 12 <= vmg.Length; i++, p += 12)
        {
            // Entry: [0]=playback type, [1]=angles, [2..3]=chapters(BE),
            // [4..5]=parental, [6]=VTS number, [7]=VTS title index,
            // [8..11]=VTS starting sector.
            int angles = vmg[p + 1] & 0x0F;
            int chapters = BinaryPrimitives.ReadUInt16BigEndian(vmg.AsSpan(p + 2));
            int vtsNo = vmg[p + 6];
            int vtsTitle = vmg[p + 7];
            list.Add(new Title
            {
                TitleNumber = i + 1,
                TitleSet = vtsNo,
                VtsTitle = vtsTitle,
                Chapters = chapters,
                AngleCount = Math.Max(1, angles),
            });
        }
        return list;
    }

    // VTS_PGCIT: the title-domain program-chain table. A 32-bit BE sector pointer sits at
    // VTSI_MAT+0xCC (relative to the VTS IFO). The table is: nr_of_pgci_srp(2), reserved(2),
    // last_byte(4), then an 8-byte PGCI_SRP per PGC whose bytes 4..7 give the PGC's byte offset
    // (relative to the table). Each PGC then has nr_programs at +0x02, nr_cells at +0x03, and a
    // 4-byte BCD playback time at +0x04.
    private static List<ProgramChain> ParseVtsPgcit(byte[] vts)
    {
        var list = new List<ProgramChain>();
        if (vts.Length < 0xD0) return list;
        uint sector = BinaryPrimitives.ReadUInt32BigEndian(vts.AsSpan(0xCC));
        if (sector == 0) return list;
        long tbl = (long)sector * 2048;
        if (tbl + 8 > vts.Length) return list;

        int nr = BinaryPrimitives.ReadUInt16BigEndian(vts.AsSpan((int)tbl));
        for (int i = 0; i < nr; i++)
        {
            int srp = (int)tbl + 8 + i * 8;
            if (srp + 8 > vts.Length) break;
            uint pgcOff = BinaryPrimitives.ReadUInt32BigEndian(vts.AsSpan(srp + 4));
            long pgc = tbl + pgcOff;
            if (pgc + 8 > vts.Length) break;

            int programs = vts[(int)pgc + 0x02];
            int cells = vts[(int)pgc + 0x03];
            int seconds = DecodeBcdTime(vts.AsSpan((int)pgc + 0x04, 4));
            list.Add(new ProgramChain { Programs = programs, Cells = cells, DurationSeconds = seconds });
        }
        return list;
    }

    /// <summary>Decode a DVD PGC playback time: bytes are BCD hour, minute, second, and a
    /// frame byte whose top two bits are the frame rate. Returns whole seconds.</summary>
    private static int DecodeBcdTime(ReadOnlySpan<byte> t)
    {
        static int Bcd(byte b) => (b >> 4) * 10 + (b & 0x0F);
        return Bcd(t[0]) * 3600 + Bcd(t[1]) * 60 + Bcd(t[2]);
    }

    // VTS IFO: audio/subtitle attributes for the TITLE domain live at fixed
    // offsets. Audio count at 0x203 (8-bit), attrs from 0x204 (8 bytes each,
    // up to 8). Subpicture count at 0x255, attrs from 0x256 (6 bytes each).
    private static (IReadOnlyList<AudioStream>, IReadOnlyList<SubtitleStream>) ParseVtsStreams(byte[] vts)
    {
        var audio = new List<AudioStream>();
        var subs = new List<SubtitleStream>();

        if (vts.Length > 0x204)
        {
            int aCount = Math.Min(vts[0x203], (byte)8);
            for (int i = 0; i < aCount; i++)
            {
                int a = 0x204 + i * 8;
                if (a + 8 > vts.Length) break;
                int codingMode = (vts[a] >> 5) & 0x7;
                string codec = codingMode switch
                {
                    0 => "AC3", 2 => "MPEG1", 3 => "MPEG2", 4 => "LPCM", 6 => "DTS", _ => "?",
                };
                int channels = (vts[a + 1] & 0x7) + 1;
                // Language: two ASCII bytes at a+2..a+3 if the "language present"
                // bits (a>>2 & 3 == 1) are set.
                string lang = "  ";
                if (((vts[a] >> 2) & 0x3) == 1 && a + 3 < vts.Length)
                    lang = $"{(char)vts[a + 2]}{(char)vts[a + 3]}";
                audio.Add(new AudioStream
                {
                    Index = i, Codec = codec, Language = lang.Trim(), Channels = channels,
                });
            }
        }

        if (vts.Length > 0x256)
        {
            int sCount = Math.Min(vts[0x255], (byte)32);
            for (int i = 0; i < sCount; i++)
            {
                int s = 0x256 + i * 6;
                if (s + 6 > vts.Length) break;
                string lang = "  ";
                // In subp_attr_t the language TYPE is the low two bits of byte 0
                // (code_mode:3, zero1:3, type:2); type == 1 means a 2-char code
                // follows at bytes +2..+3. (Real discs set 0x01 here, not 0x80 —
                // testing bit 7 read every disc's subtitle language as blank.)
                if ((vts[s] & 0x03) == 1 && s + 3 < vts.Length)
                    lang = $"{(char)vts[s + 2]}{(char)vts[s + 3]}";
                subs.Add(new SubtitleStream { Index = i, Language = lang.Trim() });
            }
        }

        return (audio, subs);
    }

    private static bool HasMagic(byte[] ifo, string magic)
    {
        if (ifo.Length < magic.Length) return false;
        for (int i = 0; i < magic.Length; i++)
            if (ifo[i] != (byte)magic[i]) return false;
        return true;
    }
}

public sealed class IfoFormatException(string message) : Exception(message);
