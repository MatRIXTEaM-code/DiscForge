// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.GameCube;
using DiscForge.Core.PlayStation;
using DiscForge.Core.Raw;
using DiscForge.Core.Xbox;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// OPTIONAL cross-tool interop / oracle tests. The unit tests elsewhere prove the codecs are
/// internally correct and spec-conformant; these upgrade that to oracle-validation when a real
/// third-party sample is available — the last mile called out in docs/ECM.md, docs/PSX_MEDIA.md,
/// docs/RVZ.md and the GameCube junk work.
///
/// They are inert by default: point the environment variable <c>DFORGE_FIXTURES</c> at a
/// directory to activate them. Expected layout (any subset may be present):
///
///   $DFORGE_FIXTURES/ecm/reference.ecm           an .ecm made by the original ecm tool
///   $DFORGE_FIXTURES/ecm/reference.bin           its exact original raw image
///   $DFORGE_FIXTURES/mdec/reference.str          a real PlayStation .str video
///   $DFORGE_FIXTURES/gamecube/reference.iso      a real UN-SCRUBBED GameCube ISO (junk intact)
///   $DFORGE_FIXTURES/rvz/reference.rvz           a real GameCube .rvz
///   $DFORGE_FIXTURES/rvz/reference.iso           its known-good decoded ISO
///   $DFORGE_FIXTURES/god/header                  a real Xbox 360 GOD header (+ header.data/Data####)
///   $DFORGE_FIXTURES/rvz/wii.rvz                  a real Wii .rvz (structure only — no decryption)
///
/// A reference sample is only data (a binary test vector), not code, so keeping one as a fixture
/// stays inside the clean-room boundary. When the directory or a file is absent the test returns
/// without asserting, so CI stays green with no fixtures checked in.
/// </summary>
public class InteropFixtureTests
{
    private static string? FixtureDir()
    {
        string? dir = Environment.GetEnvironmentVariable("DFORGE_FIXTURES");
        return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : null;
    }

    [Fact]
    public void Ecm_decodes_a_reference_file_byte_for_byte()
    {
        string? dir = FixtureDir();
        if (dir is null) return;                                   // no fixtures — inert
        string ecm = Path.Combine(dir, "ecm", "reference.ecm");
        string bin = Path.Combine(dir, "ecm", "reference.bin");
        if (!File.Exists(ecm) || !File.Exists(bin)) return;

        using var inp = File.OpenRead(ecm);
        using var outMs = new MemoryStream();
        EcmCodec.Decode(inp, outMs);

        // Byte-for-byte agreement with an .ecm produced by the original tool is the
        // interop guarantee that a self-round-trip cannot give.
        Assert.Equal(File.ReadAllBytes(bin), outMs.ToArray());
    }

    [Fact]
    public void Mdec_decodes_every_frame_of_a_reference_str_without_error()
    {
        string? dir = FixtureDir();
        if (dir is null) return;
        string str = Path.Combine(dir, "mdec", "reference.str");
        if (!File.Exists(str)) return;

        StrDemuxResult demux;
        using (var fs = File.OpenRead(str))
            demux = StrDemuxer.Demux(fs, StrDemuxer.Layout.Raw2352);

        Assert.NotEmpty(demux.Frames);
        int decoded = 0;
        foreach (var f in demux.Frames)
        {
            var img = MdecFrameDecoder.DecodeFrame(f.Bitstream, f.Width, f.Height);
            Assert.Equal(f.Width * f.Height * 4, img.Rgba.Length);
            decoded++;
        }
        // A real STR decoding end-to-end without hitting "Invalid AC variable-length
        // code" is strong evidence the Table B-14 VLC and bit order are correct.
        Assert.True(decoded > 0);
    }

    [Fact]
    public void GameCube_junk_generator_matches_a_real_unscrubbed_disc()
    {
        // The oracle for the clean-room LFG: a genuine un-scrubbed GameCube ISO carries Nintendo's
        // real junk. GcJunkReconstructor regenerates the disc's OWN surviving junk and compares it
        // byte-for-byte; SelfValidated == true means our generator reproduces the real disc exactly.
        // A FALSE result here is the signal that the LFG constants still need correcting against this
        // oracle (see docs/RVZ.md / the gc-junk-fill note) — which is exactly why this fixture exists.
        string? dir = FixtureDir();
        if (dir is null) return;
        string iso = Path.Combine(dir, "gamecube", "reference.iso");
        if (!File.Exists(iso)) return;

        using var input = File.OpenRead(iso);
        using var output = new MemoryStream();
        var report = GcJunkReconstructor.Reconstruct(input, output);

        Assert.True(report.IntactRegionsChecked > 0, "expected surviving junk on an un-scrubbed disc");
        Assert.True(report.SelfValidated,
            "the clean-room junk generator did NOT reproduce this real disc's junk — the LFG needs correction.");
    }

    [Fact]
    public void Rvz_decoder_reproduces_a_real_rvz_data_regions()
    {
        // Oracle for the RVZ container walk + zstd + offset math against a real Dolphin .rvz. Our
        // decoder zero-fills the Nintendo junk (documented), so we compare where OUR output is
        // non-zero — every real data byte must match the known-good ISO. This validates the whole
        // machinery (raw-data/group tables, group decompression, RVZ-packed unpack) end to end.
        string? dir = FixtureDir();
        if (dir is null) return;
        string rvz = Path.Combine(dir, "rvz", "reference.rvz");
        string iso = Path.Combine(dir, "rvz", "reference.iso");
        if (!File.Exists(rvz) || !File.Exists(iso)) return;

        using var output = new MemoryStream();
        var report = RvzDecoder.Decode(File.ReadAllBytes(rvz), output);
        var ours = output.ToArray();
        var reference = File.ReadAllBytes(iso);

        Assert.Equal(reference.Length, ours.Length);
        long mismatches = 0, firstAt = -1;
        for (int i = 0; i < ours.Length; i++)
            if (ours[i] != 0 && ours[i] != reference[i]) { mismatches++; if (firstAt < 0) firstAt = i; }
        Assert.True(mismatches == 0,
            $"RVZ data region mismatch vs the reference ISO ({mismatches:N0} bytes, first at 0x{firstAt:X}).");
        Assert.True(report.Groups > 0);
    }

    [Fact]
    public void God_extracts_a_real_package_to_a_valid_xdvdfs_image()
    {
        // Oracle for the GOD block→offset formula: a real GOD package must reconstruct to a valid XDVDFS
        // ISO. Success here means one of the two block conventions self-validated against the disc's own
        // descriptor — pinning the formula the public references disagreed on.
        string? dir = FixtureDir();
        if (dir is null) return;
        string header = Path.Combine(dir, "god", "header");
        if (!File.Exists(header)) return;

        using var output = new MemoryStream();
        var result = GodExtractor.Extract(header, output);
        Assert.True(result.Succeeded,
            "GOD reconstruction declined on a real package — neither block convention produced a valid " +
            "XDVDFS image, so the formula still isn't pinned: " + result.Detail);
        Assert.True(XdvdfsReader.IsXdvdfs(new MemoryStream(output.ToArray())));
    }

    [Fact]
    public void Wii_rvz_partition_structure_reads_without_decryption()
    {
        // A real Wii .rvz must map to its partitions from the unencrypted structure alone (no keys). This
        // validates the RVZ raw-data prefix decode + Wii partition-table parse against a real file; the
        // encrypted ISO rebuild remains deliberately out of scope.
        string? dir = FixtureDir();
        if (dir is null) return;
        string rvz = Path.Combine(dir, "rvz", "wii.rvz");
        if (!File.Exists(rvz)) return;

        var vol = RvzDecoder.ReadWiiStructure(File.ReadAllBytes(rvz));
        Assert.NotEmpty(vol.Partitions);
        Assert.Contains(vol.Partitions, p => p.Type == WiiPartitionType.Data);
    }
}
