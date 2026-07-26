// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

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
public static class IfoReader
{
    public sealed record AudioStream
    {
        public required int Index { get; init; }
        public required string Codec { get; init; }   // AC3, DTS, LPCM, MPEG
        public required string Language { get; init; } // ISO 639 2-letter, or "  "
        public int Channels { get; init; }
    }

    public sealed record SubtitleStream
    {
        public required int Index { get; init; }
        public required string Language { get; init; }
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

    public sealed record TitleSet
    {
        public required int Number { get; init; }          // VTS_nn
        public required long MenuVobBytes { get; init; }   // VTS_nn_0.VOB
        public required long TitleVobBytes { get; init; }  // VTS_nn_1.VOB … n.VOB summed
        public IReadOnlyList<Title> Titles { get; init; } = Array.Empty<Title>();
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
                // Language present when the top bits of byte s are set (0x9 pattern).
                if ((vts[s] & 0x80) != 0 && s + 3 < vts.Length)
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
