// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Cue;
using DiscForge.Core.Raw;
using DiscForge.Core.Util;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Subchannel-faithful copying — the CloneCD / BlindWrite capability that
/// mattered for console backups. The test that matters: deliberately-corrupt
/// Q sub-channel (LibCrypt-style protection) must survive a verbatim write
/// UNCHANGED, and DiscForge's normal Q generation must be shown to "repair"
/// it — which is exactly why the verbatim path has to exist.
/// </summary>
public class SubchannelFidelityTests
{
    private const int Sectors = 4000;

    /// <summary>
    /// LibCrypt corrupts a handful of Q frames in deliberate, scattered
    /// positions — few enough to read as a signature rather than damage, and
    /// spread so a single bad read can't mimic it. Two clusters is the
    /// characteristic shape.
    ///
    /// Every entry must fall inside <see cref="Sectors"/>. AuthorSubcode only
    /// writes that many frames, so an out-of-range LBA is silently never
    /// corrupted — and the test then blames the analyser for failing to find
    /// something that was never put there. This list previously ran to 5200 on
    /// a 4000-sector disc and did exactly that.
    /// </summary>
    private static readonly int[] Corrupt = { 1400, 1401, 1402, 1403, 3200, 3201 };

    private static byte[] AuthorSubcode()
    {
        var sub = new byte[Sectors * 96];
        for (int s = 0; s < Sectors; s++)
        {
            var q = SubQ.Position(QControl.Data, 1, 1,
                Msf.FromSectors(s), Msf.FromSectors(s + 150));
            for (int i = 0; i < 96; i++)
                if ((q[i >> 3] & (0x80 >> (i & 7))) != 0)
                    sub[s * 96 + i] |= 0x40;
            if (Array.IndexOf(Corrupt, s) >= 0)
                sub[s * 96 + 30] ^= 0x40;         // deliberate Q corruption
        }
        return sub;
    }

    [Fact]
    public void Every_corrupt_sector_is_inside_the_disc()
    {
        // Guards the mistake this suite actually shipped with: an LBA past the
        // end of the disc is never corrupted, so the analyser correctly finds
        // nothing there and the failure looks like an analyser fault.
        Assert.All(Corrupt, s => Assert.InRange(s, 0, Sectors - 1));
    }

    [Fact]
    public void Analyser_RecognisesLibCryptFingerprint()
    {
        using var sub = new MemoryStream(AuthorSubcode());
        var a = RawSubchannel.Analyse(sub);

        Assert.Equal(Corrupt.Length, a.QInvalid);
        Assert.Equal(Sectors - Corrupt.Length, a.QValid);
        Assert.True(a.LooksLikeLibCrypt);
        Assert.Equal(Corrupt.Select(x => (long)x), a.InvalidLbas);
    }

    [Fact]
    public void Analyser_CleanDiscHasNoFingerprint()
    {
        var bytes = AuthorSubcode();
        // Repair the corrupt frames so every Q is valid.
        for (int s = 0; s < Sectors; s++)
            if (Array.IndexOf(Corrupt, s) >= 0)
                bytes[s * 96 + 30] ^= 0x40;
        using var sub = new MemoryStream(bytes);
        var a = RawSubchannel.Analyse(sub);

        Assert.Equal(0, a.QInvalid);
        Assert.False(a.LooksLikeLibCrypt);
    }

    [Fact]
    public void Analyser_ManyErrorsAreDamageNotProtection()
    {
        var bytes = AuthorSubcode();
        // Wreck a fifth of the frames — a bad rip, not a protection pattern.
        for (int s = 0; s < Sectors; s += 5) bytes[s * 96 + 30] ^= 0x40;
        using var sub = new MemoryStream(bytes);
        var a = RawSubchannel.Analyse(sub);

        Assert.True(a.QInvalid > 64);
        Assert.False(a.LooksLikeLibCrypt);
    }

    private static long[] CorruptSectorsAfterGenerate(bool verbatim)
    {
        var subBytes = AuthorSubcode();
        var pcm = new byte[Sectors * 2352];
        new Random(77).NextBytes(pcm);
        using var bin = new MemoryStream(pcm);
        using var sub = new MemoryStream(subBytes);

        var track = new RawTrack
        {
            Number = 1, Mode = RawTrackMode.Audio, Control = QControl.None,
            LengthSectors = Sectors, PregapGeneratedSectors = 150,
            Source = bin, SourceByteOffset = 0, StoredSectorSize = 2352,
            SubSource = sub, SubByteOffset = 0, SubVerbatim = verbatim,
        };
        using var layout = new DiscLayout { Tracks = new[] { track } };
        using var img = new MemoryStream();
        RawImageGenerator.Generate(layout, RawSubcodeForm.Packed96, img);

        long leadIn = RawImageGenerator.LeadInSectors;
        var bad = new List<long>();
        var subOut = new byte[96];
        var q = new byte[12];
        for (int s = 0; s < Sectors; s++)
        {
            img.Position = (leadIn + 150 + s) * 2448L + 2352;
            img.ReadExactly(subOut, 0, 96);
            SubcodeFrame.ExtractQ(subOut, RawSubcodeForm.Packed96, q);
            if (Crc16.ComputeInverted(q.AsSpan(0, 10)) != (ushort)((q[10] << 8) | q[11]))
                bad.Add(s);
        }
        return bad.ToArray();
    }

    [Fact]
    public void Verbatim_PreservesExactlyTheCorruptSectors()
    {
        var bad = CorruptSectorsAfterGenerate(verbatim: true);
        Assert.Equal(Corrupt.Select(x => (long)x), bad);
    }

    [Fact]
    public void NonVerbatim_RepairsQ_WhichIsWhyVerbatimExists()
    {
        var bad = CorruptSectorsAfterGenerate(verbatim: false);
        Assert.Empty(bad);   // our own Q is always valid — protection destroyed
    }

    [Fact]
    public void Verbatim_RoundTripsByteIdentical()
    {
        // The whole 96-byte frame, not just Q, must survive unchanged.
        var subBytes = AuthorSubcode();
        var pcm = new byte[Sectors * 2352];
        new Random(5).NextBytes(pcm);
        using var bin = new MemoryStream(pcm);
        using var sub = new MemoryStream(subBytes);

        var track = new RawTrack
        {
            Number = 1, Mode = RawTrackMode.Audio, Control = QControl.None,
            LengthSectors = Sectors, PregapGeneratedSectors = 150,
            Source = bin, SourceByteOffset = 0, StoredSectorSize = 2352,
            SubSource = sub, SubByteOffset = 0, SubVerbatim = true,
        };
        using var layout = new DiscLayout { Tracks = new[] { track } };
        using var img = new MemoryStream();
        RawImageGenerator.Generate(layout, RawSubcodeForm.Interleaved96, img);

        long leadIn = RawImageGenerator.LeadInSectors;
        var frame = new byte[96];
        for (int s = 0; s < Sectors; s++)
        {
            img.Position = (leadIn + 150 + s) * 2448L + 2352;
            img.ReadExactly(frame, 0, 96);
            Assert.True(frame.AsSpan().SequenceEqual(subBytes.AsSpan(s * 96, 96)),
                $"sector {s} sub-channel differs");
        }
    }

    [Fact]
    public void Layout_FlagsVerbatimForNegotiation()
    {
        using var bin = new MemoryStream(new byte[Sectors * 2352]);
        using var sub = new MemoryStream(AuthorSubcode());
        var track = new RawTrack
        {
            Number = 1, Mode = RawTrackMode.Audio, Control = QControl.None,
            LengthSectors = Sectors, Source = bin, SourceByteOffset = 0,
            StoredSectorSize = 2352, SubSource = sub, SubByteOffset = 0,
            SubVerbatim = true,
        };
        using var layout = new DiscLayout { Tracks = new[] { track } };
        Assert.True(layout.HasVerbatimSubchannel);
        Assert.False(layout.HasProgramRw);   // verbatim is not CD+G passthrough
    }
}