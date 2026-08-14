// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DiscForge.Core.Util;

namespace DiscForge.Core.Preservation;

/// <summary>How a protected file compares to what the PAR2 set recorded for it.</summary>
public enum Par2FileStatus : byte { Ok, Corrupt, Missing }

/// <summary>One file a PAR2 set protects: its recorded identity (name, length, whole-file MD5) and the
/// per-slice checksums, plus — after a verify — how it actually compares on disk.</summary>
public sealed record Par2ProtectedFile
{
    public required byte[] FileId { get; init; }
    public required string Name { get; init; }
    public required long Length { get; init; }
    public required byte[] FileMd5 { get; init; }
    public required byte[] Md5_16k { get; init; }
    public required IReadOnlyList<byte[]> SliceMd5 { get; init; }
    public required IReadOnlyList<uint> SliceCrc32 { get; init; }

    public int SliceCount => SliceMd5.Count;

    public Par2FileStatus Status { get; init; } = Par2FileStatus.Ok;
    /// <summary>Slices that are missing or fail their checksum (all of them when the file is absent).</summary>
    public int DamagedSlices { get; init; }
}

/// <summary>The outcome of reading and verifying a PAR2 recovery set.</summary>
public sealed record Par2VerifyResult
{
    public required byte[] RecoverySetId { get; init; }
    public required long SliceSize { get; init; }
    public required IReadOnlyList<Par2ProtectedFile> Files { get; init; }
    /// <summary>Recovery slices available across all the set's `.par2` volumes — the repair budget.</summary>
    public required int RecoverySlices { get; init; }
    public string? Creator { get; init; }
    public required int PacketsParsed { get; init; }
    /// <summary>PAR2 packets whose own MD5 didn't check out — the recovery data itself is damaged.</summary>
    public required int BadPackets { get; init; }

    public int TotalDataSlices => Files.Sum(f => f.SliceCount);
    public int DamagedSlices => Files.Sum(f => f.DamagedSlices);
    public bool AllOk => Files.All(f => f.Status == Par2FileStatus.Ok) && BadPackets == 0;
    /// <summary>The damage is within the recovery budget — PAR2 could rebuild the missing/broken slices.</summary>
    public bool Repairable => DamagedSlices <= RecoverySlices;

    public string Summary()
    {
        int ok = Files.Count(f => f.Status == Par2FileStatus.Ok);
        int corrupt = Files.Count(f => f.Status == Par2FileStatus.Corrupt);
        int missing = Files.Count(f => f.Status == Par2FileStatus.Missing);
        var sb = new StringBuilder(
            $"PAR2 set: {Files.Count} file(s), {SliceSize:N0}-byte slices, {RecoverySlices} recovery slice(s). ");
        if (AllOk)
            sb.Append($"All {ok} file(s) verify — repair not required.");
        else
        {
            sb.Append($"{ok} OK, {corrupt} corrupt, {missing} missing; ");
            sb.Append($"{DamagedSlices} of {TotalDataSlices} data slice(s) damaged — ");
            sb.Append(Repairable
                ? $"repairable ({RecoverySlices} recovery slice(s) cover it)."
                : $"NOT repairable (need {DamagedSlices - RecoverySlices} more recovery slice(s)).");
        }
        if (BadPackets > 0) sb.Append($" ⚠ {BadPackets} PAR2 packet(s) are themselves damaged.");
        return sb.ToString();
    }
}

/// <summary>
/// par2-verify — read and verify a PAR2 (Parchive 2.0) recovery set, the de-facto redundancy format for
/// long-term "cold storage" of a file collection. DiscForge has its own Reed-Solomon vault; this makes it
/// interoperate with the `.par2` sets people already keep beside their archives: it walks the packet
/// structure, checks each packet's own MD5 (so damage to the recovery data itself is caught), lists the
/// protected files with their recorded hashes, verifies those files on disk slice by slice, and reports
/// whether the damage found is within the available recovery slices — i.e. whether par2 could repair it.
/// Read/verify only: it reports repairability, it does not perform the Reed-Solomon reconstruction.
/// </summary>
public static class Par2
{
    private static readonly byte[] Magic = "PAR2\0PKT"u8.ToArray();
    private const string TypeMain = "PAR 2.0\0Main";
    private const string TypeFileDesc = "PAR 2.0\0FileDesc";
    private const string TypeIfsc = "PAR 2.0\0IFSC";
    private const string TypeRecvSlic = "PAR 2.0\0RecvSlic";
    private const string TypeCreator = "PAR 2.0\0Creator";

    /// <summary>Parse a set and verify its files against the copies in the `.par2`'s own directory.</summary>
    public static Par2VerifyResult Verify(string mainPar2Path)
    {
        ArgumentNullException.ThrowIfNull(mainPar2Path);
        if (!File.Exists(mainPar2Path)) throw new FileNotFoundException("PAR2 file not found.", mainPar2Path);

        string dir = Path.GetDirectoryName(Path.GetFullPath(mainPar2Path)) ?? ".";
        var (result, files) = ParseSet(mainPar2Path, dir);

        var verified = new List<Par2ProtectedFile>(files.Count);
        foreach (var f in files) verified.Add(VerifyFile(f, dir, result.SliceSize));

        return result with { Files = verified };
    }

    /// <summary>Parse the set's structure only (no on-disk file checks).</summary>
    public static Par2VerifyResult Read(string mainPar2Path)
    {
        ArgumentNullException.ThrowIfNull(mainPar2Path);
        string dir = Path.GetDirectoryName(Path.GetFullPath(mainPar2Path)) ?? ".";
        return ParseSet(mainPar2Path, dir).Result;
    }

    // ---- parsing ------------------------------------------------------------

    private static (Par2VerifyResult Result, IReadOnlyList<Par2ProtectedFile> Files) ParseSet(string mainPath, string dir)
    {
        // Read the main file first to pin the recovery-set id, then fold in sibling .par2 volumes for the
        // recovery-slice count. Dedup every packet by its own MD5 (par2 repeats packets across volumes).
        var seenPackets = new HashSet<string>();
        int packets = 0, bad = 0;

        long sliceSize = 0;
        byte[]? setId = null;
        var fileOrder = new List<byte[]>();
        var fileDescs = new Dictionary<string, (string Name, long Length, byte[] Md5, byte[] Md5_16k)>();
        var ifsc = new Dictionary<string, (List<byte[]> Md5, List<uint> Crc)>();
        var recoveryExponents = new HashSet<uint>();
        string? creator = null;

        void Ingest(byte[] data)
        {
            foreach (var p in WalkPackets(data))
            {
                string md5Key = System.Convert.ToHexString(p.PacketMd5);
                if (!seenPackets.Add(md5Key)) continue;   // already ingested this exact packet
                packets++;
                if (!p.Md5Ok) { bad++; continue; }        // damaged recovery data — don't trust its body

                switch (p.Type)
                {
                    case TypeMain when p.Body.Length >= 12:
                        sliceSize = BinaryPrimitives.ReadInt64LittleEndian(p.Body.AsSpan(0, 8));
                        int nf = BinaryPrimitives.ReadInt32LittleEndian(p.Body.AsSpan(8, 4));
                        setId ??= p.SetId;
                        if (fileOrder.Count == 0)
                            for (int k = 0; k < nf && 12 + (k + 1) * 16 <= p.Body.Length; k++)
                                fileOrder.Add(p.Body[(12 + k * 16)..(12 + k * 16 + 16)]);
                        break;

                    case TypeFileDesc when p.Body.Length >= 56:
                        string fid = System.Convert.ToHexString(p.Body.AsSpan(0, 16));
                        var name = Encoding.UTF8.GetString(p.Body[56..]).TrimEnd('\0');
                        fileDescs[fid] = (name,
                            BinaryPrimitives.ReadInt64LittleEndian(p.Body.AsSpan(48, 8)),
                            p.Body[16..32], p.Body[32..48]);
                        break;

                    case TypeIfsc when p.Body.Length >= 16:
                        string ifid = System.Convert.ToHexString(p.Body.AsSpan(0, 16));
                        if (!ifsc.ContainsKey(ifid))
                        {
                            var md5s = new List<byte[]>();
                            var crcs = new List<uint>();
                            for (int off = 16; off + 20 <= p.Body.Length; off += 20)
                            {
                                md5s.Add(p.Body[off..(off + 16)]);
                                crcs.Add(BinaryPrimitives.ReadUInt32LittleEndian(p.Body.AsSpan(off + 16, 4)));
                            }
                            ifsc[ifid] = (md5s, crcs);
                        }
                        break;

                    case TypeRecvSlic when p.Body.Length >= 4:
                        recoveryExponents.Add(BinaryPrimitives.ReadUInt32LittleEndian(p.Body.AsSpan(0, 4)));
                        break;

                    case TypeCreator:
                        creator ??= Encoding.UTF8.GetString(p.Body).TrimEnd('\0').Trim();
                        break;
                }
            }
        }

        Ingest(File.ReadAllBytes(mainPath));
        // Fold in the other .par2 volumes beside it (recovery slices live there).
        foreach (var sib in Directory.EnumerateFiles(dir, "*.par2"))
            if (!string.Equals(Path.GetFullPath(sib), Path.GetFullPath(mainPath), StringComparison.OrdinalIgnoreCase))
                try { Ingest(File.ReadAllBytes(sib)); } catch { /* skip unreadable sibling */ }

        var files = new List<Par2ProtectedFile>();
        // Preserve the Main packet's file order; fall back to whatever FileDesc packets we saw.
        IEnumerable<byte[]> order = fileOrder.Count > 0 ? fileOrder
            : fileDescs.Keys.Select(k => System.Convert.FromHexString(k));
        foreach (var idBytes in order)
        {
            string fid = System.Convert.ToHexString(idBytes);
            if (!fileDescs.TryGetValue(fid, out var fd)) continue;
            var slices = ifsc.TryGetValue(fid, out var s) ? s : (Md5: new List<byte[]>(), Crc: new List<uint>());
            files.Add(new Par2ProtectedFile
            {
                FileId = idBytes,
                Name = fd.Name,
                Length = fd.Length,
                FileMd5 = fd.Md5,
                Md5_16k = fd.Md5_16k,
                SliceMd5 = slices.Md5,
                SliceCrc32 = slices.Crc,
            });
        }

        var result = new Par2VerifyResult
        {
            RecoverySetId = setId ?? Array.Empty<byte>(),
            SliceSize = sliceSize,
            Files = files,
            RecoverySlices = recoveryExponents.Count,
            Creator = creator,
            PacketsParsed = packets,
            BadPackets = bad,
        };
        return (result, files);
    }

    private readonly record struct Packet(string Type, byte[] SetId, byte[] PacketMd5, byte[] Body, bool Md5Ok);

    // Walk the PAR2 packet stream, resynchronising on the magic so a corrupt length can't derail the scan.
    private static IEnumerable<Packet> WalkPackets(byte[] d)
    {
        int i = 0, n = d.Length;
        while (i + 64 <= n)
        {
            if (!d.AsSpan(i, 8).SequenceEqual(Magic)) { i++; continue; }
            long len = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(i + 8, 8));
            // A packet must at least hold its 64-byte header and fit in the buffer.
            // (The spec pads packet lengths to a 4-byte multiple, but do NOT hard-reject
            // on that — the MD5 check below is the real integrity gate, and rejecting on
            // alignment would silently drop an otherwise valid, unpadded packet.)
            if (len < 64 || i + len > n) { i += 8; continue; }

            var packetMd5 = d[(i + 16)..(i + 32)];
            var setId = d[(i + 32)..(i + 48)];
            string type = Encoding.ASCII.GetString(d, i + 48, 16).TrimEnd('\0');
            var body = d[(i + 64)..(i + (int)len)];
            // The packet MD5 covers everything from the set-id (offset 0x20) to the packet end.
            bool ok = MD5.HashData(d.AsSpan(i + 32, (int)len - 32)).AsSpan().SequenceEqual(packetMd5);

            yield return new Packet(type, setId, packetMd5, body, ok);
            i += (int)len;
        }
    }

    // ---- verification -------------------------------------------------------

    private static Par2ProtectedFile VerifyFile(Par2ProtectedFile f, string dir, long sliceSize)
    {
        string path = Path.Combine(dir, f.Name);
        if (!File.Exists(path))
            return f with { Status = Par2FileStatus.Missing, DamagedSlices = f.SliceCount };

        byte[] bytes = File.ReadAllBytes(path);
        // Fast path: the whole-file MD5 and length are the authoritative identity check.
        if (bytes.LongLength == f.Length && MD5.HashData(bytes).AsSpan().SequenceEqual(f.FileMd5))
            return f with { Status = Par2FileStatus.Ok, DamagedSlices = 0 };

        // Something differs — pin down how many slices are damaged, for the repair budget.
        int ss = (int)sliceSize;
        int damaged = 0;
        var slice = new byte[ss];
        for (int k = 0; k < f.SliceCount; k++)
        {
            long start = (long)k * ss;
            if (start >= bytes.LongLength) { damaged++; continue; }   // slice past the (truncated) file
            int have = (int)Math.Min(ss, bytes.LongLength - start);
            Array.Clear(slice);
            Array.Copy(bytes, start, slice, 0, have);
            uint crc = Crc32.Compute(slice);
            if (crc != f.SliceCrc32[k] || !MD5.HashData(slice).AsSpan().SequenceEqual(f.SliceMd5[k]))
                damaged++;
        }
        return f with { Status = Par2FileStatus.Corrupt, DamagedSlices = damaged };
    }
}
