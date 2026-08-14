// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Security.Cryptography;
using DiscForge.Core.Cue;
using DiscForge.Core.Patch;
using DiscForge.Core.Redump;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The redump.org submission-info generator (software half): for a bin/cue dump it
/// reports each track's exact CRC-32 / MD5 / SHA-1 and size and a whole-image combined
/// hash. The checks recompute the same hashes independently over the on-disk track files
/// and require an exact match, and confirm the cuesheet is carried through verbatim and
/// the rendered text contains the checksums.
/// </summary>
public class SubmissionTests
{
    private static (string Crc, string Md5, string Sha1) Hashes(byte[] d) =>
    (
        BpsPatch.Crc32(d).ToString("x8"),
        System.Convert.ToHexString(MD5.HashData(d)).ToLowerInvariant(),
        System.Convert.ToHexString(SHA1.HashData(d)).ToLowerInvariant()
    );

    [Fact]
    public void Submission_info_hashes_every_track_and_the_whole_image()
    {
        var dir = Directory.CreateTempSubdirectory("dforge_sub_").FullName;
        try
        {
            // A data track and an audio track, one bin file each (a multi-FILE cue).
            var t1 = new byte[2352 * 60]; for (int i = 0; i < t1.Length; i++) t1[i] = (byte)((i * 7) % 251);
            var t2 = new byte[2352 * 40]; new Random(5).NextBytes(t2);
            File.WriteAllBytes(Path.Combine(dir, "t1.bin"), t1);
            File.WriteAllBytes(Path.Combine(dir, "t2.bin"), t2);
            string cue =
                "FILE \"t1.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n" +
                "FILE \"t2.bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 01 00:00:00\n";
            string cuePath = Path.Combine(dir, "disc.cue");
            File.WriteAllText(cuePath, cue);

            var info = SubmissionInfoGenerator.Generate(cuePath);

            Assert.Equal(2, info.Tracks.Count);
            var (c1, m1, s1) = Hashes(t1);
            var (c2, m2, s2) = Hashes(t2);

            Assert.Equal(CueTrackType.Mode1_2352, info.Tracks[0].Type);
            Assert.Equal(t1.Length, info.Tracks[0].Size);
            Assert.Equal(60, info.Tracks[0].Sectors);
            Assert.Equal(c1, info.Tracks[0].Crc32);
            Assert.Equal(m1, info.Tracks[0].Md5);
            Assert.Equal(s1, info.Tracks[0].Sha1);

            Assert.Equal(CueTrackType.Audio, info.Tracks[1].Type);
            Assert.Equal(c2, info.Tracks[1].Crc32);
            Assert.Equal(s2, info.Tracks[1].Sha1);

            Assert.Equal(t1.Length + t2.Length, info.TotalSize);

            // Combined = hash over the concatenation of the two tracks, in order.
            var combined = new byte[t1.Length + t2.Length];
            t1.CopyTo(combined, 0); t2.CopyTo(combined, t1.Length);
            var (cc, cm, cs) = Hashes(combined);
            Assert.Equal(cc, info.CombinedCrc32);
            Assert.Equal(cm, info.CombinedMd5);
            Assert.Equal(cs, info.CombinedSha1);

            // Cuesheet carried verbatim; rendered text shows the checksums.
            Assert.Contains("TRACK 01 MODE1/2352", info.Cuesheet);
            string text = info.ToRedumpText();
            Assert.Contains(c1, text);
            Assert.Contains(cc, text);
            Assert.Contains("Cuesheet:", text);
            Assert.Contains("Size & Checksums", text);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_single_track_image_reports_the_bin_hash_as_the_combined_hash()
    {
        var dir = Directory.CreateTempSubdirectory("dforge_sub2_").FullName;
        try
        {
            var bin = new byte[2352 * 30]; for (int i = 0; i < bin.Length; i++) bin[i] = (byte)((i + 3) % 240);
            File.WriteAllBytes(Path.Combine(dir, "d.bin"), bin);
            File.WriteAllText(Path.Combine(dir, "d.cue"),
                "FILE \"d.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n");

            var info = SubmissionInfoGenerator.Generate(Path.Combine(dir, "d.cue"));
            var (crc, _, sha1) = Hashes(bin);
            Assert.Single(info.Tracks);
            Assert.Equal(crc, info.Tracks[0].Crc32);
            Assert.Equal(crc, info.CombinedCrc32);   // one track => combined equals it
            Assert.Equal(sha1, info.CombinedSha1);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_missing_image_is_declined()
    {
        Assert.Throws<FileNotFoundException>(() =>
            SubmissionInfoGenerator.Generate(Path.Combine(Path.GetTempPath(), "nope_" + Guid.NewGuid() + ".cue")));
    }

    private static SubmissionInfo Bare(DiscForge.Core.PlayStation.PsDiscId? id) => new()
    {
        FileName = "x.cdi",
        InputFormat = "CDI",
        Tracks = Array.Empty<TrackSubmission>(),
        TotalSize = 0,
        CombinedCrc32 = "00000000",
        CombinedMd5 = "d41d8cd98f00b204e9800998ecf8427e",
        CombinedSha1 = "da39a3ee5e6b4b0d3255bfef95601890afd80709",
        Cuesheet = "FILE \"x.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n",
        PlayStationId = id,
    };

    [Fact]
    public void A_playstation_disc_auto_fills_region_and_a_detected_serial_block()
    {
        // The one part of Common Disc Info that IS in the image: the serial/region from SYSTEM.CNF.
        var id = DiscForge.Core.PlayStation.SystemCnf.Parse("BOOT = cdrom:\\SLUS_007.57;1\nVER = 1.1\nVMODE = NTSC\n");
        string text = Bare(id).ToRedumpText();

        Assert.Contains("Region: USA (NTSC-U)", text);         // filled from the serial's third letter
        Assert.Contains("Detected from image (SYSTEM.CNF", text);
        Assert.Contains("SLUS-00757", text);                    // normalised serial
        Assert.Contains("PlayStation 1", text);
        Assert.Contains("Video mode: NTSC", text);
        Assert.Contains("Title: ", text);                       // marketing name still left blank for the submitter
    }

    [Fact]
    public void A_non_playstation_disc_leaves_region_blank_and_omits_the_detected_block()
    {
        string text = Bare(null).ToRedumpText();
        Assert.Contains("\tRegion: \n".Replace("\n", Environment.NewLine), text);
        Assert.DoesNotContain("Detected from image", text);
    }
}
