// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.VideoCd;

/// <summary>The kind of PSD list descriptor.</summary>
public enum PsdDescriptorType : byte
{
    Unused = 0x00,
    PlayList = 0x10,
    SelectionList = 0x18,
    ExtendedSelectionList = 0x1A,   // SVCD
    EndList = 0x1F,
}

/// <summary>A play list: a sequence of items to play, with links to the previous/next/return list.</summary>
public sealed record PsdPlayList(
    int Lid, bool Rejected, int PlayTime, int WaitTime, int AutoPauseWait,
    int PrevOffset, int NextOffset, int ReturnOffset, IReadOnlyList<int> Items);

/// <summary>A selection list — a menu: a set of numbered selections, each linking to another list.</summary>
public sealed record PsdSelectionList(
    int Lid, bool Rejected, int Flags, int BaseSelectionNumber, int NumberOfSelections,
    int PrevOffset, int NextOffset, int ReturnOffset, int DefaultOffset, int TimeoutOffset,
    int TimeoutTime, int Loop, int ItemId, IReadOnlyList<int> SelectionOffsets, bool Extended);

/// <summary>An end list — terminates a play sequence, optionally changing disc or picture.</summary>
public sealed record PsdEndList(int NextDisc, int ChangePicture);

/// <summary>One descriptor in the PSD, with the byte offset at which it was found.</summary>
public sealed record PsdDescriptor(
    int ByteOffset, PsdDescriptorType Type,
    PsdPlayList? PlayList = null, PsdSelectionList? Selection = null, PsdEndList? End = null)
{
    public int Lid => PlayList?.Lid ?? Selection?.Lid ?? -1;
}

/// <summary>The whole decoded PSD, with offset resolution across descriptors.</summary>
public sealed record PsdDocument
{
    public required IReadOnlyList<PsdDescriptor> Descriptors { get; init; }

    /// <summary>Resolve a PSD offset (8-byte units; 0xFFFF = none) to a descriptor index, or -1.</summary>
    public int Resolve(int offsetUnits)
    {
        if (offsetUnits is VcdPsd.OffsetNone or < 0) return -1;
        int bytePos = offsetUnits * 8;
        for (int i = 0; i < Descriptors.Count; i++)
            if (Descriptors[i].ByteOffset == bytePos) return i;
        return -1;
    }

    public int PlayLists => Descriptors.Count(d => d.Type == PsdDescriptorType.PlayList);
    public int Menus => Descriptors.Count(d => d.Type is PsdDescriptorType.SelectionList or PsdDescriptorType.ExtendedSelectionList);

    public string Summary()
        => Descriptors.Count == 0
            ? "Empty PSD — no PlayBack Control."
            : $"PSD: {Descriptors.Count} descriptor(s) — {PlayLists} play list(s), {Menus} menu(s).";
}

/// <summary>
/// vcd-psd — the decoder for a Video CD's PlayBack Control, the interactive layer that turns a disc into a
/// menued title. PBC lives in PSD.VCD as a chain of list descriptors: a <b>play list</b> plays a sequence
/// of items and links to the previous/next/return list; a <b>selection list</b> is a menu whose numbered
/// selections each jump to another list; an <b>end list</b> closes a sequence. Descriptors reference one
/// another by offsets counted in 8-byte units (0xFFFF meaning "none"), and LOT.VCD indexes lists by their
/// LID. This walks PSD.VCD descriptor by descriptor, decodes each type, and resolves the offsets so the
/// menu graph — which selection goes where — can be read out. Follows the VCD 2.0 / White Book PSD
/// structure. Read-only; it parses and reports.
/// </summary>
public static class VcdPsd
{
    /// <summary>A PSD offset value meaning "no link".</summary>
    public const int OffsetNone = 0xFFFF;

    /// <summary>Parse PSD.VCD into its descriptor list.</summary>
    public static PsdDocument Parse(ReadOnlySpan<byte> psd)
    {
        var list = new List<PsdDescriptor>();
        int pos = 0;
        while (pos + 8 <= psd.Length)
        {
            byte type = psd[pos];
            if (type == (byte)PsdDescriptorType.Unused) break;   // padding / end of table

            switch (type)
            {
                case (byte)PsdDescriptorType.PlayList:
                {
                    int noi = psd[pos + 1];
                    int size = Align8(14 + 2 * noi);
                    if (pos + size > psd.Length) return Done(list);
                    int lidRaw = U16(psd, pos + 2);
                    var items = new int[noi];
                    for (int i = 0; i < noi; i++) items[i] = U16(psd, pos + 14 + 2 * i);
                    list.Add(new PsdDescriptor(pos, PsdDescriptorType.PlayList, PlayList: new PsdPlayList(
                        Lid: lidRaw & 0x7FFF, Rejected: (lidRaw & 0x8000) != 0,
                        PlayTime: U16(psd, pos + 10), WaitTime: psd[pos + 12], AutoPauseWait: psd[pos + 13],
                        PrevOffset: U16(psd, pos + 4), NextOffset: U16(psd, pos + 6), ReturnOffset: U16(psd, pos + 8),
                        Items: items)));
                    pos += size;
                    break;
                }
                case (byte)PsdDescriptorType.SelectionList:
                case (byte)PsdDescriptorType.ExtendedSelectionList:
                {
                    int nos = psd[pos + 2];
                    int size = Align8(20 + 2 * nos);
                    if (pos + size > psd.Length) return Done(list);
                    int lidRaw = U16(psd, pos + 4);
                    var ofs = new int[nos];
                    for (int i = 0; i < nos; i++) ofs[i] = U16(psd, pos + 20 + 2 * i);
                    list.Add(new PsdDescriptor(pos,
                        type == (byte)PsdDescriptorType.ExtendedSelectionList
                            ? PsdDescriptorType.ExtendedSelectionList : PsdDescriptorType.SelectionList,
                        Selection: new PsdSelectionList(
                            Lid: lidRaw & 0x7FFF, Rejected: (lidRaw & 0x8000) != 0,
                            Flags: psd[pos + 1], BaseSelectionNumber: psd[pos + 3], NumberOfSelections: nos,
                            PrevOffset: U16(psd, pos + 6), NextOffset: U16(psd, pos + 8), ReturnOffset: U16(psd, pos + 10),
                            DefaultOffset: U16(psd, pos + 12), TimeoutOffset: U16(psd, pos + 14),
                            TimeoutTime: psd[pos + 16], Loop: psd[pos + 17], ItemId: U16(psd, pos + 18),
                            SelectionOffsets: ofs, Extended: type == (byte)PsdDescriptorType.ExtendedSelectionList)));
                    pos += size;
                    break;
                }
                case (byte)PsdDescriptorType.EndList:
                {
                    list.Add(new PsdDescriptor(pos, PsdDescriptorType.EndList,
                        End: new PsdEndList(NextDisc: psd[pos + 1], ChangePicture: U16(psd, pos + 2))));
                    pos += 8;
                    break;
                }
                default:
                    // Unknown descriptor type — stop rather than misread the rest.
                    return Done(list);
            }
        }
        return Done(list);
    }

    /// <summary>Parse LOT.VCD — the List ID Offset Table mapping each LID (1-based) to a PSD offset (in
    /// 8-byte units; 0xFFFF = unused, returned as -1).</summary>
    public static IReadOnlyList<int> ReadLot(ReadOnlySpan<byte> lot)
    {
        var offsets = new List<int>();
        for (int p = 0; p + 2 <= lot.Length; p += 2)
        {
            int v = U16(lot, p);
            offsets.Add(v == OffsetNone ? -1 : v);
        }
        // Trim the trailing unused entries a LOT is padded with.
        int last = offsets.FindLastIndex(o => o >= 0);
        return last < 0 ? Array.Empty<int>() : offsets.Take(last + 1).ToList();
    }

    public static string Render(PsdDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var sb = new StringBuilder();
        sb.AppendLine(doc.Summary());
        foreach (var d in doc.Descriptors)
        {
            switch (d.Type)
            {
                case PsdDescriptorType.PlayList when d.PlayList is { } pl:
                    sb.AppendLine($"  @{d.ByteOffset / 8} PlayList LID {pl.Lid}: items [{string.Join(", ", pl.Items)}], " +
                                  $"next {LinkName(doc, pl.NextOffset)}, return {LinkName(doc, pl.ReturnOffset)}");
                    break;
                case PsdDescriptorType.SelectionList or PsdDescriptorType.ExtendedSelectionList when d.Selection is { } sl:
                    sb.AppendLine($"  @{d.ByteOffset / 8} {(sl.Extended ? "ExtMenu" : "Menu")} LID {sl.Lid}: " +
                                  $"{sl.NumberOfSelections} selection(s) from #{sl.BaseSelectionNumber} → " +
                                  $"[{string.Join(", ", sl.SelectionOffsets.Select(o => LinkName(doc, o)))}]");
                    break;
                case PsdDescriptorType.EndList when d.End is { } el:
                    sb.AppendLine($"  @{d.ByteOffset / 8} EndList (next disc {el.NextDisc})");
                    break;
            }
        }
        return sb.ToString().TrimEnd();
    }

    // ---- helpers ------------------------------------------------------------

    private static string LinkName(PsdDocument doc, int offset)
    {
        if (offset == OffsetNone) return "-";
        int idx = doc.Resolve(offset);
        if (idx < 0) return $"ofs {offset}";
        var d = doc.Descriptors[idx];
        return d.Lid >= 0 ? $"LID {d.Lid}" : d.Type.ToString();
    }

    private static PsdDocument Done(List<PsdDescriptor> list) => new() { Descriptors = list };
    private static int U16(ReadOnlySpan<byte> b, int o) => (b[o] << 8) | b[o + 1];   // big-endian
    private static int Align8(int n) => (n + 7) & ~7;
}
