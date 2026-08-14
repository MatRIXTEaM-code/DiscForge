// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Preservation;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the DiscForge Preservation Master: building a master for an image records its per-file fixity;
/// verifying an untouched file passes; and flipping a single byte fails verification on the hashes and the
/// Merkle root. A missing member file is reported rather than throwing.
/// </summary>
public class PreservationMasterTests
{
    private static byte[] Content()
    {
        var b = new byte[128 * 1024];
        for (int i = 0; i < b.Length; i++) b[i] = (byte)((i * 2654435761u) >> 24);
        return b;
    }

    [Fact]
    public void Build_records_fixity_and_verify_round_trips()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string path = Path.Combine(dir, "image.bin");
            File.WriteAllBytes(path, Content());

            var master = PreservationMasterBuilder.Build(path);
            Assert.Single(master.Files);
            var entry = master.Files[0];
            Assert.Equal("image.bin", entry.Name);
            Assert.NotEmpty(entry.Sha256);
            Assert.NotEmpty(entry.MerkleRoot);

            var (ok, diffs) = PreservationMasterBuilder.VerifyFile(entry, dir);
            Assert.True(ok);
            Assert.Empty(diffs);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_flipped_byte_fails_verification_on_hashes_and_merkle()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string path = Path.Combine(dir, "image.bin");
            File.WriteAllBytes(path, Content());
            var entry = PreservationMasterBuilder.Build(path).Files[0];

            var b = File.ReadAllBytes(path);
            b[10_000] ^= 0xFF;
            File.WriteAllBytes(path, b);

            var (ok, diffs) = PreservationMasterBuilder.VerifyFile(entry, dir);
            Assert.False(ok);
            Assert.Contains(diffs, d => d.Contains("SHA-256"));
            Assert.Contains(diffs, d => d.Contains("Merkle"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_missing_member_is_reported_not_thrown()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var entry = new MasterFileEntry
            {
                Name = "gone.bin", Length = 1, Crc32 = "0", Md5 = "0", Sha1 = "0", Sha256 = "0", MerkleRoot = "0",
            };
            var (ok, diffs) = PreservationMasterBuilder.VerifyFile(entry, dir);
            Assert.False(ok);
            Assert.Contains(diffs, d => d.Contains("missing"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
