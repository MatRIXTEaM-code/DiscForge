// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Xbox;

/// <summary>The result of a GOD → ISO reconstruction attempt.</summary>
public sealed record GodExtractResult
{
    public required bool Succeeded { get; init; }
    /// <summary>Which block-numbering convention produced a valid image (0 or 1), or -1 if none did.</summary>
    public required int Convention { get; init; }
    public required long IsoBytes { get; init; }
    public required string Detail { get; init; }
}

/// <summary>
/// Reconstructs the Xbox 360 disc image (XDVDFS ISO) from a GOD (Games on Demand) package by walking its
/// 0x1000-byte data blocks and skipping the interleaved SHA-1 hash-table blocks. The public references
/// disagree on the block→offset formula by one hash block (free60's worked example vs py360's code), and a
/// single off-by-one silently corrupts the whole output — so, per "provably correct or declined", this does
/// not trust either formula blindly. It reconstructs with BOTH conventions and accepts a result only when it
/// is a valid XDVDFS volume (the "MICROSOFT*XBOX*MEDIA" descriptor appears at a documented base) — the disc's
/// own structure is the oracle that resolves the ambiguity. If neither convention yields a valid image it
/// DECLINES rather than write a shifted, corrupt ISO. It decrypts nothing and never touches the RSA signature.
///
/// The layout it walks (read-only STFS / GOD): data is stored in 0x1000-byte blocks with a level-0 SHA-1 hash
/// block before every 0xAA (170) data blocks, a level-1 hash block every 0xAA level-0 blocks, and a level-2
/// hash block every 0xAA level-1 blocks. The two conventions differ only in whether the hash block that opens
/// a group is counted at the group boundary or one block earlier.
/// </summary>
public static class GodExtractor
{
    private const int BlockSize = 0x1000;
    private const int GroupBlocks = 0xAA;                 // data blocks per level-0 hash table
    private const long L1 = (long)GroupBlocks * GroupBlocks;
    private const long L2 = L1 * GroupBlocks;

    /// <summary>Reconstruct the ISO from a GOD header file. Writes to <paramref name="output"/> only on
    /// success; returns a report saying which convention validated (or that it declined).</summary>
    public static GodExtractResult Extract(string headerPath, Stream output)
    {
        ArgumentNullException.ThrowIfNull(headerPath);
        ArgumentNullException.ThrowIfNull(output);

        var info = GodContainer.Read(headerPath);
        if (!info.LooksLikeGamesOnDemand)
            return Decline($"not a Games-on-Demand package (content type 0x{info.ContentType:X4}, expected 0x7000).");
        if (info.DataFiles.Count == 0)
            return Decline("no Data#### payload files were found next to the header.");

        long isoBytes = info.ContentSize > 0 ? info.ContentSize : DefaultIsoLength(info);
        long dataBlocks = (isoBytes + BlockSize - 1) / BlockSize;

        // Concatenated block stream across all Data#### files (each is a flat run of 0x1000-byte blocks).
        using var stream = new ConcatStream(info.DataFiles.Select(f => f.Path).ToList());
        long totalPhysBlocks = stream.Length / BlockSize;

        for (int convention = 0; convention <= 1; convention++)
        {
            var iso = TryReconstruct(stream, dataBlocks, isoBytes, totalPhysBlocks, convention);
            if (iso is not null && XdvdfsReader.IsXdvdfs(new MemoryStream(iso, false)))
            {
                output.Write(iso, 0, iso.Length);
                return new GodExtractResult
                {
                    Succeeded = true,
                    Convention = convention,
                    IsoBytes = iso.Length,
                    Detail = $"reconstructed {iso.Length:N0} bytes; block convention {convention} validated against the XDVDFS descriptor.",
                };
            }
        }

        return Decline(
            "neither block convention produced a valid XDVDFS image — the block→offset formula could not be " +
            "resolved from this package alone. A reference GOD+ISO fixture is needed to pin it (docs/XBOX.md).");
    }

    private static byte[]? TryReconstruct(ConcatStream stream, long dataBlocks, long isoBytes,
                                          long totalPhysBlocks, int convention)
    {
        var iso = new byte[isoBytes];
        var block = new byte[BlockSize];
        for (long db = 0; db < dataBlocks; db++)
        {
            long phys = PhysicalBlock(db, convention);
            if (phys < 0 || phys >= totalPhysBlocks) return null;      // ran past the payload → wrong convention
            stream.Position = phys * BlockSize;
            stream.ReadExactly(block, 0, BlockSize);
            long dst = db * BlockSize;
            int n = (int)Math.Min(BlockSize, isoBytes - dst);
            Array.Copy(block, 0, iso, dst, n);
        }
        return iso;
    }

    /// <summary>Map a logical data-block index to its physical block index, skipping the hash blocks that
    /// precede it. Convention 0 counts the group-opening hash block at the boundary; convention 1 offsets it
    /// by one — the documented one-block ambiguity, which the XDVDFS self-check resolves.</summary>
    private static long PhysicalBlock(long dataBlock, int convention)
    {
        long hashes;
        if (convention == 0)
        {
            hashes = dataBlock / GroupBlocks + 1;
            hashes += dataBlock / L1 + 1;
            hashes += dataBlock / L2 + 1;
        }
        else
        {
            hashes = (dataBlock + 1) / GroupBlocks;
            hashes += (dataBlock + 1) / L1;
            hashes += (dataBlock + 1) / L2;
            hashes += 1;    // the master (level-2) hash block at the head of the stream
        }
        return dataBlock + hashes;
    }

    private static long DefaultIsoLength(GodInfo info)
    {
        // Fall back to the payload's block count minus a generous hash-block allowance when the header
        // didn't record a content size (rare). Rounded down to a whole sector.
        long payloadBlocks = info.DataFilesTotal / BlockSize;
        long approxData = payloadBlocks - payloadBlocks / GroupBlocks - 4;
        return Math.Max(0, approxData) * BlockSize;
    }

    private static GodExtractResult Decline(string why) =>
        new() { Succeeded = false, Convention = -1, IsoBytes = 0, Detail = "declined — " + why };

    /// <summary>A read-only stream that presents several files back-to-back as one contiguous stream,
    /// so the block walk can address the whole GOD payload without concatenating it in memory.</summary>
    private sealed class ConcatStream : Stream
    {
        private readonly List<(FileStream Fs, long Start, long Len)> _parts = new();
        private long _position;
        public override long Length { get; }

        public ConcatStream(IReadOnlyList<string> paths)
        {
            long start = 0;
            foreach (var p in paths)
            {
                var fs = File.OpenRead(p);
                _parts.Add((fs, start, fs.Length));
                start += fs.Length;
            }
            Length = start;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Position { get => _position; set => _position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (count > 0 && _position < Length)
            {
                var (fs, pstart, plen) = _parts.First(x => _position >= x.Start && _position < x.Start + x.Len);
                fs.Position = _position - pstart;
                int want = (int)Math.Min(count, pstart + plen - _position);
                int n = fs.Read(buffer, offset, want);
                if (n <= 0) break;
                _position += n; offset += n; count -= n; total += n;
            }
            return total;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => Length + offset,
            };
            return _position;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var p in _parts) p.Fs.Dispose();
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
