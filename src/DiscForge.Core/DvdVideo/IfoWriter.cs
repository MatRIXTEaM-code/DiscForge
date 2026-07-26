// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

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
/// Scope — honest: this emits the *structural* IFO (enumeration, streams,
/// budgeting map). It does not compose the navigation tables a hardware player
/// walks to actually play a disc — the PGCI (program-chain / cell playback),
/// C_ADT (cell address table) and VOBU_ADMAP — because those must be generated in
/// lock-step with the muxed VOBs, and producing genuinely playable output remains
/// the job of the <c>dvdauthor</c> runner the reauthor plan drives. What this adds
/// is a native, dependency-free writer for the structural layer: round-trippable,
/// unit-testable, and the foundation a fuller navigation emitter builds on.
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
        const int sectors = 1;   // the structural header fits one sector
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

        return ifo;
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
            e[0] = 0x80;
            e[2] = (byte)lang[0];
            e[3] = (byte)lang[1];
        }
    }
}
