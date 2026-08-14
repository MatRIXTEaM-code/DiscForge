// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Media;

/// <summary>How a dual-layer DVD's two layers are addressed: Parallel Track Path (both layers run the
/// same direction, layer 1 restarts at the inner edge) or Opposite Track Path (layer 1 runs back inward,
/// its addresses inverted) — the near-universal choice for pressed dual-layer video/game discs.</summary>
public enum DvdTrackPath { Parallel, Opposite }

/// <summary>
/// The physical layout a DVD's Physical Format Information (PFI) declares — book type, layer count,
/// track path, the data-area extent, and, for a dual-layer disc, the <b>layer break</b>: the LBA at
/// which layer 0 ends and layer 1 begins. A faithful dual-layer dump has to record this, because the
/// break is a physical property not present in the ISO data, and PS2 / Xbox 360 / PC DVD-9 images are
/// only reconstructable to the original disc with it. Purely descriptive — read from the PFI a drive
/// reports (the <c>.physical</c> sidecar DiscImageCreator saves), it changes nothing.
/// </summary>
public sealed record DvdPhysicalLayout
{
    public required int BookType { get; init; }
    public required string BookTypeName { get; init; }
    public required int Layers { get; init; }
    public required DvdTrackPath TrackPath { get; init; }

    /// <summary>First physical sector number of the data area — 0x30000 on a standard DVD-ROM.</summary>
    public required uint DataStartPsn { get; init; }
    /// <summary>Last physical sector number of the data area.</summary>
    public required uint DataEndPsn { get; init; }
    /// <summary>Last physical sector number of layer 0 (0 when the disc is single-layer).</summary>
    public required uint Layer0EndPsn { get; init; }

    /// <summary>Total data-area sectors across all layers.</summary>
    public long TotalDataSectors => DataEndPsn >= DataStartPsn ? DataEndPsn - DataStartPsn + 1 : 0;

    /// <summary>The layer break as a 0-based LBA into the image — the number of sectors on layer 0, and
    /// where layer 1 begins. Null on a single-layer disc, or when the PFI carries no layer-0 end.</summary>
    public long? LayerBreak =>
        Layers >= 2 && Layer0EndPsn > DataStartPsn ? Layer0EndPsn - DataStartPsn + 1 : null;

    public long? Layer0Sectors => LayerBreak;
    public long? Layer1Sectors => LayerBreak is { } lb ? TotalDataSectors - lb : null;

    /// <summary>Check the declared layout for internal consistency, and — when the dumped image's sector
    /// count is known — that the image matches the data area. <paramref name="imageSectors"/> is the
    /// image size in 2048-byte sectors (file length ÷ 2048), or null to skip that cross-check.</summary>
    public IReadOnlyList<string> Verify(long? imageSectors = null)
    {
        var w = new List<string>();
        if (DataStartPsn != 0x30000)
            w.Add($"data area starts at PSN 0x{DataStartPsn:X} (a standard DVD-ROM starts at 0x30000)");
        if (TotalDataSectors <= 0)
            w.Add("the data area end is not after its start — the PFI looks malformed");

        if (Layers >= 2)
        {
            if (LayerBreak is null)
                w.Add("dual-layer disc but the PFI carries no layer-0 end sector — the layer break is unknown");
            else if (LayerBreak >= TotalDataSectors)
                w.Add($"layer break {LayerBreak} is not inside the data area ({TotalDataSectors} sectors)");
        }
        else if (Layer0EndPsn > DataStartPsn && Layer0EndPsn < DataEndPsn)
            w.Add("single-layer disc, but the PFI records a layer-0 end short of the data end — inconsistent");

        if (imageSectors is { } img && TotalDataSectors > 0 && img != TotalDataSectors)
            w.Add($"image is {img:N0} sector(s) but the PFI declares {TotalDataSectors:N0} data sector(s) " +
                  $"({(img > TotalDataSectors ? "over" : "under")}-sized by {Math.Abs(img - TotalDataSectors):N0})");
        return w;
    }

    public bool IsConsistent(long? imageSectors = null) => Verify(imageSectors).Count == 0;

    public string Summary(long? imageSectors = null)
    {
        var sb = new StringBuilder($"{BookTypeName}, {Layers} layer(s)");
        if (Layers >= 2) sb.Append($", {(TrackPath == DvdTrackPath.Opposite ? "OTP" : "PTP")}");
        sb.Append($"; data area PSN 0x{DataStartPsn:X}–0x{DataEndPsn:X} ({TotalDataSectors:N0} sectors)");
        if (LayerBreak is { } lb)
            sb.Append($"; layer break at LBA {lb:N0} (L0 {Layer0Sectors:N0} + L1 {Layer1Sectors:N0})");
        else if (Layers >= 2)
            sb.Append("; layer break UNKNOWN");
        var w = Verify(imageSectors);
        sb.Append(w.Count == 0 ? " — consistent." : " — " + string.Join("; ", w) + ".");
        return sb.ToString();
    }
}

/// <summary>Parser for DVD Physical Format Information (PFI), format 0x00 of READ DISC STRUCTURE. Layout
/// per ECMA-267 (after any 4-byte MMC response header):
///   +0  book type (bits 7-4) / part version (bits 3-0)
///   +1  disc size (bits 7-4) / max rate (bits 3-0)
///   +2  bits 6-5 number of layers-1, bit 4 track path (0=PTP,1=OTP), bits 3-0 layer type
///   +4..+7   start physical sector number of the data area
///   +8..+11  end physical sector number of the data area
///   +12..+15 end physical sector number in layer 0
/// </summary>
public static class DvdPhysicalFormat
{
    private const uint StandardDataStart = 0x30000;

    /// <summary>Parse PFI bytes, tolerating either a raw PFI block or a full READ DISC STRUCTURE response
    /// (2-byte length + 2 reserved, then PFI). Returns null if there aren't enough bytes.</summary>
    public static DvdPhysicalLayout? Parse(ReadOnlySpan<byte> data)
    {
        int b = LocatePfi(data);
        if (b < 0 || b + 16 > data.Length) return null;

        int book = (data[b] >> 4) & 0x0F;
        int layers = ((data[b + 2] >> 5) & 0x03) + 1;
        var path = (data[b + 2] & 0x10) != 0 ? DvdTrackPath.Opposite : DvdTrackPath.Parallel;

        uint dataStart = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(b + 4, 4));
        uint dataEnd = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(b + 8, 4));
        uint layer0End = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(b + 12, 4));

        return new DvdPhysicalLayout
        {
            BookType = book,
            BookTypeName = MediaIdentityParser.BookTypeName(book),
            Layers = layers,
            TrackPath = path,
            DataStartPsn = dataStart,
            DataEndPsn = dataEnd,
            Layer0EndPsn = layer0End,
        };
    }

    public static DvdPhysicalLayout? ParseFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Parse(File.ReadAllBytes(path));
    }

    // Decide whether the PFI starts at offset 0 (bare) or 4 (behind an MMC response header) by finding the
    // one at which the data-area start reads as the standard 0x30000; fall back to the header form.
    private static int LocatePfi(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 12 && BinaryPrimitives.ReadUInt32BigEndian(d.Slice(4, 4)) == StandardDataStart) return 0;
        if (d.Length >= 16 && BinaryPrimitives.ReadUInt32BigEndian(d.Slice(8, 4)) == StandardDataStart) return 4;
        // Neither matched the standard start (recordable/odd disc): prefer the MMC-header form when the
        // reserved bytes are zero and there's room, else treat as bare.
        if (d.Length >= 24 && d[2] == 0 && d[3] == 0) return 4;
        return d.Length >= 16 ? 0 : -1;
    }
}
