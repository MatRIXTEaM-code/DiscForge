// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using DiscForge.Core.Chd;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The CHD v5 compressed hunk-map decoder and its use in extraction, on a real
/// chdman-produced CD CHD whose map contains SELF hunks (hunks identical to an
/// earlier one, which the older codec-probe walk could not resolve). The map
/// self-verifies against its own CRC-16, and a full extraction is proven by the CHD
/// SHA-1 — so this covers the decode and the SELF copy end to end.
/// </summary>
public class ChdMapTests
{
    // A tiny MODE1/2352 CD CHD (300 sectors, a repeating pattern so most hunks
    // deduplicate into SELF references), built with chdman createcd. Verified
    // byte-identical to chdman extractcd.
    private const string ChdBase64 =
        "TUNvbXBySEQAAAB8AAAABWNkbHpjZHpsY2RmbAAAAAAAAAAAAAs0wAAAAAAAAARxAAAAAAAAAHwAAEyAAAAJkH5ZaNrcr3tRI5AlPMvLwwanQIasWOdqw8CU" +
        "0mO1/Cwu4JJ7HIlcCi8AAAAAAAAAAAAAAAAAAAAAAAAAAENIVDIBAABaAAAAAAAAAABUUkFDSzoxIFRZUEU6TU9ERTFfUkFXIFNVQlRZUEU6Tk9ORSBGUkFN" +
        "RVM6MzAwIFBSRUdBUDowIFBHVFlQRTpNT0RFMSBQR1NVQjpOT05FIFBPU1RHQVA6MAAAASEAAABSUAqE+ZuygCGpadYn4D4GWl8EjVPUBLo5VwUJwVUk3p24" +
        "cVkxYKGf+W9Jc/LI6oy6GospaSGA/jODZq9GbeyeiYoLg/A8DomOP+1f556Q2Rz/MvSy4DlRstIUFbTFcbrbBuN5mp+7OMGwAKyTC6oGGQMSCBVbm8hI8DIu" +
        "/i2gh8jwpODSUeuNZ1aSsk2ExfGGMd9qYlvCeS3Z9zxzunR0B9g8qVYiJKFm+FqEXzBn0vZLSS5/IOvb+BAOlHh3xz9r77TNleJv9kRuBs8LghrL23rwV42Y" +
        "/5DAPubBEkF17gMolusT+6cozK8yu6QOJfJYsN7YVhxm8OIbOXb5l/+Po8gv9K3y2zgxMHrAdyIkheoCBAKhPETlIRivMUIAY2AYBaNg5AIAAAEhAEAgS9h3" +
        "gqjuvKZY8JNiJmfFlFF5+x2GixkqYucNE/dx4/i8N0OMudg4yr2ln66sqZ0C/M4Urk+rB8wu3sFy7aHovgH0rGtIHEU9giyuKp/gip0ggpL7zGQR6Hhj4x34" +
        "ulH5/7Q77fcb419zcDcsUZuLLM6Cz1qNFGvR3Hqh0NM5C1HmW9tcuM1EEl2Cckof183tkSsJ2QCV+cpwfojU0p6+IKA5tpVmTO73ZSicxPVOqM9k6dvB9mOA" +
        "1HX+QNxF9z09sn2Bo5M7WszwdQA6lQqnGMXPgFHvxt7GFCydQFe8Fx5uTg6qqsq80L/Un1JOVOaQvxffGg18M48I4npTLPy5k12l75CWF+YaRwbTHPgI4EGO" +
        "JyHkd1kytbayx7feAGNgGAWjYOQCAAABJQBAIEvYd4Ko7rymWPCTYiZnxZRRefsdhosZKmLnDRP3ceP4vDdDjLnYOMq9pZ+urKmdAvzOFK5PqwfMLt7Bcu2h" +
        "6L4B9KxrSBxFPYIsriqf4IqdIIKS+8xkEeh4Y+Md+LpR+f+0O+33G+Nfc3A3LFGbiyzOgs9ajRRr0dx6odDTOQtR5lvbXLjNRBJdgnJKH9fN7ZErCdkAlfnK" +
        "cH6I1NKeviCgObaVZkzu92UonMT1TqjPZOnbwfZjgNR1/kDcRfc9PbJ9gaOTO1rM8HUAOpUKpxjFz4BR78bexhQsnUBXvBcebk4OqqrKvNC/1J9STlTmkL8X" +
        "3xoNfDOPCOJ6Uyz8uZNdpe+QlhfmGkcG0tS6PvBCCnmnmoA46dzIBJdxVLQud8kAY2AYBaNg5AIAAAAAGQAAAAAA5gbICQAAADEBERADIQIAttttttttsS2V" +
        "apaaEcx8BcA=";

    private static byte[] Sample() => System.Convert.FromBase64String(ChdBase64);

    private static (long mapOffset, int hunks, int hunkBytes, int unitBytes) Hdr(byte[] chd)
    {
        long mapOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(chd.AsSpan(0x28));
        long logical = (long)BinaryPrimitives.ReadUInt64BigEndian(chd.AsSpan(0x20));
        int hunkBytes = (int)BinaryPrimitives.ReadUInt32BigEndian(chd.AsSpan(0x38));
        int unitBytes = (int)BinaryPrimitives.ReadUInt32BigEndian(chd.AsSpan(0x3C));
        int hunks = (int)((logical + hunkBytes - 1) / hunkBytes);
        return (mapOffset, hunks, hunkBytes, unitBytes);
    }

    [Fact]
    public void The_map_decodes_and_self_verifies_with_self_hunks_present()
    {
        var chd = Sample();
        var (mapOffset, hunks, hunkBytes, unitBytes) = Hdr(chd);
        var map = ChdMap.Decode(chd, mapOffset, hunks, hunkBytes, unitBytes);

        Assert.Equal(hunks, map.Length);
        Assert.Contains(map, e => e.Type == ChdHunkType.Self);
        for (int h = 0; h < map.Length; h++)
            if (map[h].Type == ChdHunkType.Self)
                Assert.True(map[h].Offset < h, "a SELF hunk must reference an earlier hunk");
    }

    [Fact]
    public void Extraction_resolves_self_hunks_and_matches_the_chd_sha1()
    {
        var result = ChdExtractor.ExtractCd(Sample());
        Assert.True(result.Verified);
        Assert.Equal(1, result.Tracks);
        Assert.Equal(300 * 2352, result.Bin.Length);
    }
}
