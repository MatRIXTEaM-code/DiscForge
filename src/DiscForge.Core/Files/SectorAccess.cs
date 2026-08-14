// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cdi;
using DiscForge.Core.Cue;
using DiscForge.Core.Raw;

namespace DiscForge.Core.Files;

/// <summary>
/// One sector at a time, from anywhere: images implement this via
/// <see cref="SectorAccess"/>, live drives via the Devices layer. The sector
/// viewer talks only to this, so a disc in a drive and a file on disk are
/// the same thing to it.
/// </summary>
public interface ISectorSource : IDisposable
{
    /// <summary>Human description: "Cdi, 331,170 sectors" / "TSSTcorp SE-208DB, CD-ROM".</summary>
    string Description { get; }
    long TotalSectors { get; }
    SectorAccess.SectorData Read(long fileIndex);
    /// <summary>LBA, mm:ss:ff, or +fileindex → file/disc sector index.</summary>
    long Resolve(string address);
}

/// <summary>
/// Random access to the sectors of ANY image DiscForge understands, through
/// one interface — the foundation of the sector viewer and sector extraction
/// (CDRWIN's "Sector Viewer" and "Extract Sectors", reborn):
///
///   .cdi        — tracks mapped via the descriptor; stored size per track
///   .iso        — 2048-byte sectors, LBA = file index
///   raw images  — 2368/2448 with subcode (detected by Q-CRC voting), or
///                 bare 2352; lead-in accounted for in the addressing
///
/// Addresses: a plain sector index into the image, with LBA and MSF derived
/// per kind. For CDI, LBA follows the descriptor (session 2+ tracks keep
/// their real LBAs); for raw DAO images, file index 22,500 + 0 is
/// MSF 00:00:00 (LBA −150).
/// </summary>
public sealed class SectorAccess : ISectorSource
{
    public enum ImageKind { Cdi, Iso, Bin2352, RawDao }

    public sealed record SectorData
    {
        /// <summary>Index of the sector within the image file.</summary>
        public required long FileIndex { get; init; }
        public required long Lba { get; init; }
        public required Msf Msf { get; init; }
        /// <summary>The stored bytes: 2048/2336/2352 main channel.</summary>
        public required byte[] Stored { get; init; }
        /// <summary>Track number, where the image knows tracks.</summary>
        public int? Track { get; init; }
        public int? Session { get; init; }
        /// <summary>Subcode bytes, for raw images that carry them.</summary>
        public byte[]? Subcode { get; init; }
        public RawSubcodeForm? SubcodeForm { get; init; }
        /// <summary>True when this raw image sector is in the lead-in.</summary>
        public bool LeadIn { get; init; }
    }

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly CdiImage? _cdi;
    private readonly int _sectorSize;              // ISO/BIN/raw kinds
    private readonly RawSubcodeForm? _form;        // raw images
    private readonly long _leadIn;                 // raw images

    public ImageKind Kind { get; }
    public long TotalSectors { get; }
    public string Description => $"{Kind}, {TotalSectors:N0} sectors";

    private SectorAccess(Stream stream, bool owns, ImageKind kind, CdiImage? cdi,
                         int sectorSize, RawSubcodeForm? form, long leadIn, long total)
    {
        _stream = stream; _ownsStream = owns; Kind = kind; _cdi = cdi;
        _sectorSize = sectorSize; _form = form; _leadIn = leadIn; TotalSectors = total;
    }

    public void Dispose() { if (_ownsStream) _stream.Dispose(); }

    /// <summary>Open an image file, working out what it is from extension and
    /// content. Raw layouts are detected by Q-CRC voting.</summary>
    public static SectorAccess Open(string path)
    {
        var stream = File.OpenRead(path);
        try { return Open(stream, Path.GetExtension(path), owns: true); }
        catch { stream.Dispose(); throw; }
    }

    public static SectorAccess Open(Stream stream, string extensionHint = "", bool owns = false)
    {
        if (extensionHint.Equals(".cdi", StringComparison.OrdinalIgnoreCase))
        {
            var cdi = CdiParser.Parse(stream);
            long total = cdi.AllTracks.Sum(t => (long)t.TotalSectors);
            return new SectorAccess(stream, owns, ImageKind.Cdi, cdi, 0, null, 0, total);
        }
        if (extensionHint.Equals(".iso", StringComparison.OrdinalIgnoreCase))
            return new SectorAccess(stream, owns, ImageKind.Iso, null, 2048, null, 0,
                stream.Length / 2048);

        // Everything else: let the raw detector vote. A bare 2352 BIN comes
        // back with form == null.
        var (size, form) = RawImageInspector.DetectLayout(stream);
        if (form is null)
            return new SectorAccess(stream, owns, ImageKind.Bin2352, null, 2352, null, 0,
                stream.Length / 2352);

        long leadIn = RawImageInspector.FindLeadInLength(stream, size, form.Value);
        return new SectorAccess(stream, owns, ImageKind.RawDao, null, size, form, leadIn,
            stream.Length / size);
    }

    /// <summary>Read one sector by file index.</summary>
    public SectorData Read(long fileIndex)
    {
        if (fileIndex < 0 || fileIndex >= TotalSectors)
            throw new ArgumentOutOfRangeException(nameof(fileIndex),
                $"Sector {fileIndex:N0} is outside the image (0..{TotalSectors - 1:N0}).");

        switch (Kind)
        {
            case ImageKind.Cdi:
            {
                long remaining = fileIndex;
                foreach (var t in _cdi!.AllTracks)
                {
                    if (remaining >= t.TotalSectors) { remaining -= t.TotalSectors; continue; }
                    int stored = (int)t.SectorSize;
                    var data = new byte[stored];
                    _stream.Position = t.FileOffset + remaining * stored;
                    _stream.ReadExactly(data, 0, stored);
                    // LBA: the descriptor's StartLba is index 1's address; the
                    // stored pregap sits before it.
                    long lba = (long)t.StartLba - t.PregapSectors + remaining;
                    return new SectorData
                    {
                        FileIndex = fileIndex,
                        Lba = lba,
                        Msf = Msf.FromSectors(lba + 150),
                        Stored = data,
                        Track = t.Number,
                        Session = t.SessionIndex + 1,
                    };
                }
                throw new InvalidOperationException("unreachable");
            }

            case ImageKind.Iso:
            case ImageKind.Bin2352:
            {
                var data = new byte[_sectorSize];
                _stream.Position = fileIndex * _sectorSize;
                _stream.ReadExactly(data, 0, _sectorSize);
                return new SectorData
                {
                    FileIndex = fileIndex,
                    Lba = fileIndex,
                    Msf = Msf.FromSectors(fileIndex + 150),
                    Stored = data,
                };
            }

            case ImageKind.RawDao:
            {
                var main = new byte[2352];
                var sub = new byte[_sectorSize - 2352];
                _stream.Position = fileIndex * _sectorSize;
                _stream.ReadExactly(main, 0, 2352);
                _stream.ReadExactly(sub, 0, sub.Length);
                bool leadIn = fileIndex < _leadIn;
                long abs = fileIndex - _leadIn;              // MSF 00:00:00 base
                return new SectorData
                {
                    FileIndex = fileIndex,
                    Lba = leadIn ? long.MinValue : abs - 150,
                    Msf = leadIn
                        ? Msf.FromSectors(new Msf(95, 0, 0).ToSectors() + fileIndex)
                        : Msf.FromSectors(abs),
                    Stored = main,
                    Subcode = sub,
                    SubcodeForm = _form,
                    LeadIn = leadIn,
                };
            }

            default: throw new InvalidOperationException();
        }
    }

    /// <summary>
    /// Resolve an address string to a file sector index. Accepts a plain
    /// number (LBA), mm:ss:ff (absolute MSF; 95:00:00+ addresses a raw
    /// image's lead-in), or +N (file index directly).
    /// </summary>
    public long Resolve(string address)
    {
        address = address.Trim();
        if (address.StartsWith('+'))
            return long.Parse(address[1..]);

        long lba;
        if (address.Contains(':'))
        {
            long abs = Msf.Parse(address).ToSectors();
            if (Kind == ImageKind.RawDao)
            {
                long leadInStart = new Msf(95, 0, 0).ToSectors();
                // 95:00:00 and up address the lead-in (whose first sector is
                // written at 95:00:00 in DiscForge images, per the IMAPI2
                // convention); anything else is program-area absolute time.
                return abs >= leadInStart ? abs - leadInStart : _leadIn + abs;
            }
            lba = abs - 150;
        }
        else
        {
            lba = long.Parse(address);
            if (Kind == ImageKind.RawDao) return _leadIn + lba + 150;
        }

        if (Kind == ImageKind.Cdi)
        {
            // LBA → file index through the track table (sessions leave gaps in
            // LBA space that don't exist in the file).
            long fileBase = 0;
            foreach (var t in _cdi!.AllTracks)
            {
                long trackStart = (long)t.StartLba - t.PregapSectors;
                if (lba >= trackStart && lba < trackStart + t.TotalSectors)
                    return fileBase + (lba - trackStart);
                fileBase += t.TotalSectors;
            }
            throw new ArgumentOutOfRangeException(nameof(address),
                $"LBA {lba} falls outside every track of this CDI.");
        }

        return lba;
    }
}
