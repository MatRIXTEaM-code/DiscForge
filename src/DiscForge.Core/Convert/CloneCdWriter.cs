// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Globalization;
using System.Text;
using DiscForge.Core.Raw;

namespace DiscForge.Core.Convert;

/// <summary>
/// Writes the CloneCD triple — <c>.ccd</c> (INI-style TOC descriptor),
/// <c>.img</c> (raw 2352-byte main channel) and optional <c>.sub</c> (raw
/// 96-byte subchannel) — so DiscForge images interoperate with the CloneCD
/// ecosystem and the many tools that read that format.
///
/// The <c>.ccd</c> layout follows the long-published CloneCD control-file
/// structure: a [CloneCD] version stanza, a [Disc] entry-count/session block,
/// per-[Session] mode lines, and an [Entry N] block per TOC entry giving the
/// point, ADR/control, and the P/M/S/F + PLBA fields. This is a description of
/// a disc's table of contents — a data format, not a protection mechanism.
///
/// DiscForge already GENERATES the .img/.sub content through its raw image
/// generator; this class emits the accompanying .ccd so the set is complete.
/// </summary>
public static class CloneCdWriter
{
    /// <summary>
    /// Build the .ccd text for a layout. The caller writes the .img (and .sub,
    /// if <see cref="DiscLayout.HasVerbatimSubchannel"/>) via the raw generator.
    /// </summary>
    public static string BuildCcd(DiscLayout layout)
    {
        var sb = new StringBuilder();
        var nl = "\r\n";   // CloneCD files are CRLF

        // Compute the TOC entries. CloneCD encodes three "special" entries
        // (A0 = first track, A1 = last track, A2 = lead-out) plus one per track.
        var tracks = layout.Tracks.OrderBy(t => t.Number).ToList();
        int firstTrack = tracks.First().Number;
        int lastTrack = tracks.Last().Number;

        // Absolute start LBA of each track (audio pregap included as the disc
        // requires). Track 1 conventionally starts at LBA 0 in the image; the
        // MSF in the TOC is LBA + 150 (2-second lead-in offset).
        var startLba = new Dictionary<int, long>();
        long lba = 0;
        foreach (var t in tracks)
        {
            startLba[t.Number] = lba;
            lba += t.TotalSectors;
        }
        long leadOutLba = lba;

        int entryCount = tracks.Count + 3;

        sb.Append("[CloneCD]").Append(nl);
        sb.Append("Version=3").Append(nl);
        sb.Append(nl);

        sb.Append("[Disc]").Append(nl);
        sb.Append("TocEntries=").Append(entryCount.ToString(CultureInfo.InvariantCulture)).Append(nl);
        sb.Append("Sessions=1").Append(nl);
        sb.Append("DataTracksScrambled=0").Append(nl);
        sb.Append("CDTextLength=0").Append(nl);
        if (!string.IsNullOrEmpty(layout.Mcn))
            sb.Append("CATALOG=").Append(layout.Mcn).Append(nl);
        sb.Append(nl);

        // Session block.
        sb.Append("[Session 1]").Append(nl);
        // PreGapMode 2 = the usual mode-2 lead-in mode marker CloneCD emits for
        // data sessions; audio-only discs use 0. Match on first track's mode.
        bool dataDisc = tracks.Any(t => t.Mode != RawTrackMode.Audio);
        sb.Append("PreGapMode=").Append(dataDisc ? "2" : "0").Append(nl);
        sb.Append("PreGapSubC=0").Append(nl);
        sb.Append(nl);

        int entry = 0;

        // A0 — points at the first track number, PMIN carries it.
        AppendEntry(sb, nl, entry++, session: 1, point: 0xA0,
            adrControl: AdrControl(tracks.First()),
            pmin: firstTrack, psec: dataDisc ? 0x20 : 0x00, pframe: 0,
            plba: 0);

        // A1 — points at the last track number.
        AppendEntry(sb, nl, entry++, session: 1, point: 0xA1,
            adrControl: AdrControl(tracks.Last()),
            pmin: lastTrack, psec: 0, pframe: 0,
            plba: 0);

        // A2 — lead-out position (MSF from lead-out LBA).
        var (lm, ls, lf) = LbaToMsf(leadOutLba);
        AppendEntry(sb, nl, entry++, session: 1, point: 0xA2,
            adrControl: AdrControl(tracks.Last()),
            pmin: lm, psec: ls, pframe: lf,
            plba: leadOutLba);

        // One entry per track.
        foreach (var t in tracks)
        {
            var (m, s, f) = LbaToMsf(startLba[t.Number]);
            AppendEntry(sb, nl, entry++, session: 1, point: t.Number,
                adrControl: AdrControl(t),
                pmin: m, psec: s, pframe: f,
                plba: startLba[t.Number]);
        }

        return sb.ToString();
    }

    /// <summary>The .img/.sub base name a .ccd expects (same stem).</summary>
    public static (string Ccd, string Img, string Sub) NamesFor(string baseNameNoExt)
        => (baseNameNoExt + ".ccd", baseNameNoExt + ".img", baseNameNoExt + ".sub");

    private static void AppendEntry(StringBuilder sb, string nl, int index, int session,
        int point, int adrControl, long pmin, long psec, long pframe, long plba)
    {
        sb.Append("[Entry ").Append(index.ToString(CultureInfo.InvariantCulture)).Append(']').Append(nl);
        sb.Append("Session=").Append(session).Append(nl);
        sb.Append("Point=0x").Append(point.ToString("x2", CultureInfo.InvariantCulture)).Append(nl);
        sb.Append("ADR=0x").Append(((adrControl >> 4) & 0xF).ToString("x2", CultureInfo.InvariantCulture)).Append(nl);
        sb.Append("Control=0x").Append((adrControl & 0xF).ToString("x2", CultureInfo.InvariantCulture)).Append(nl);
        sb.Append("TrackNo=0").Append(nl);
        sb.Append("AMin=0").Append(nl);
        sb.Append("ASec=0").Append(nl);
        sb.Append("AFrame=0").Append(nl);
        sb.Append("ALBA=-150").Append(nl);
        sb.Append("Zero=0").Append(nl);
        sb.Append("PMin=").Append(pmin.ToString(CultureInfo.InvariantCulture)).Append(nl);
        sb.Append("PSec=").Append(psec.ToString(CultureInfo.InvariantCulture)).Append(nl);
        sb.Append("PFrame=").Append(pframe.ToString(CultureInfo.InvariantCulture)).Append(nl);
        sb.Append("PLBA=").Append(plba.ToString(CultureInfo.InvariantCulture)).Append(nl);
        sb.Append(nl);
    }

    // ADR (upper nibble) = 1 for position Q; Control (lower nibble) is exactly
    // the QControl bit pattern DiscForge already stores.
    private static int AdrControl(RawTrack t)
    {
        int control = (int)t.Control & 0xF;
        return (0x1 << 4) | control;   // ADR=1, Control=…
    }

    private static (int m, int s, int f) LbaToMsf(long lba)
    {
        long v = lba + 150;   // 2-second lead-in
        int f = (int)(v % 75); v /= 75;
        int s = (int)(v % 60); v /= 60;
        int m = (int)v;
        return (m, s, f);
    }
}
