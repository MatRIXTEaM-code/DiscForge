// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.
//
// Corpus-verification driver: a thin CLI over DiscForge.Core's CHD read/write
// paths so verify.sh can cross-check DiscForge against chdman as the oracle.
//
//   chd-driver extract   <chd> <out.bin> [parent.chd ...]   read any CHD -> raw/bin
//   chd-driver createhd  <img> <out.chd>                     write a hard-disk CHD
//   chd-driver createcd  <cue> <out.chd>                     write a CD CHD from bin/cue
//   chd-driver check     <chd> [parent.chd ...]              decode the map only (CRC self-check)
using DiscForge.Core.Chd;
using System.Buffers.Binary;

if (args.Length < 2) { Console.Error.WriteLine("usage: extract|createhd|createcd|check ..."); return 2; }
string mode = args[0];
try
{
    switch (mode)
    {
        case "extract":
        {
            byte[] chd = File.ReadAllBytes(args[1]);
            var parents = new List<byte[]>();
            for (int i = 3; i < args.Length; i++) parents.Add(File.ReadAllBytes(args[i]));
            if (ChdReader.Read(chd).IsCd)
                File.WriteAllBytes(args[2], ChdExtractor.ExtractCd(chd, parents.ToArray()).Bin);
            else
                File.WriteAllBytes(args[2], ChdHdExtractor.Extract(chd, parents.ToArray()));
            break;
        }
        case "createhd":
            File.WriteAllBytes(args[2], ChdWriter.CreateHd(File.ReadAllBytes(args[1])));
            break;
        case "createcd":
            File.WriteAllBytes(args[2],
                ChdWriter.CreateCdFromBinCue(File.ReadAllText(args[1]),
                    Path.GetDirectoryName(Path.GetFullPath(args[1]))!));
            break;
        case "check":
        {
            byte[] chd = File.ReadAllBytes(args[1]);
            long mapOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(chd.AsSpan(0x28));
            int hunkBytes = (int)BinaryPrimitives.ReadUInt32BigEndian(chd.AsSpan(0x38));
            int unitBytes = (int)BinaryPrimitives.ReadUInt32BigEndian(chd.AsSpan(0x3C));
            long logical = (long)BinaryPrimitives.ReadUInt64BigEndian(chd.AsSpan(0x20));
            int hunks = (int)((logical + hunkBytes - 1) / hunkBytes);
            var map = ChdMap.Decode(chd, mapOffset, hunks, hunkBytes, unitBytes);
            var counts = new Dictionary<ChdHunkType, int>();
            foreach (var e in map) counts[e.Type] = counts.GetValueOrDefault(e.Type) + 1;
            Console.WriteLine($"{hunks} hunks, map CRC OK, types=" +
                string.Join(",", counts.Select(kv => $"{kv.Key}:{kv.Value}")));
            break;
        }
        default:
            Console.Error.WriteLine($"unknown mode {mode}");
            return 2;
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL {mode}: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
