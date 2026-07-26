// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;
using DiscForge.Core.Cue;
using DiscForge.Core.Raw;
using DiscForge.Core.Util;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The RAW DAO pipeline, tested without hardware: checksums against published
/// vectors, ECC by independent syndrome evaluation, sub-channel layouts
/// against each other, and the generator by dissecting the images it emits.
/// </summary>
public class RawTests
{
    // ---- CRC-16 ------------------------------------------------------------

    [Fact]
    public void Crc16_MatchesXmodemVector()
    {
        Assert.Equal(0x31C3, Crc16.Compute(Encoding.ASCII.GetBytes("123456789")));
        Assert.Equal(unchecked((ushort)~0x31C3),
            Crc16.ComputeInverted(Encoding.ASCII.GetBytes("123456789")));
    }

    // ---- scrambler ---------------------------------------------------------

    [Fact]
    public void Scrambler_IsSelfInverse_AndLeavesSyncAlone()
    {
        var sector = new byte[2352];
        new Random(42).NextBytes(sector);
        var copy = (byte[])sector.Clone();

        CdScrambler.ScrambleInPlace(sector);
        Assert.False(sector.AsSpan(12).SequenceEqual(copy.AsSpan(12)));
        Assert.True(sector.AsSpan(0, 12).SequenceEqual(copy.AsSpan(0, 12)));

        CdScrambler.ScrambleInPlace(sector);
        Assert.Equal(copy, sector);
    }

    // ---- EDC / ECC ---------------------------------------------------------

    [Fact]
    public void Mode1Sector_HasValidEdc_AndZeroEccSyndromes()
    {
        var user = new byte[2048];
        new Random(7).NextBytes(user);
        var sector = new byte[2352];
        RawSectorBuilder.BuildMode1(user, new Msf(0, 2, 0), sector);

        Assert.Equal(0x00, sector[0]);
        Assert.Equal(0xFF, sector[1]);
        Assert.Equal(1, sector[15]);

        uint edc = EdcEcc.ComputeEdc(sector.AsSpan(0, 2064));
        uint stored = (uint)sector[2064] | ((uint)sector[2065] << 8)
                    | ((uint)sector[2066] << 16) | ((uint)sector[2067] << 24);
        Assert.Equal(edc, stored);

        // Every RS codeword must evaluate to zero at both generator roots —
        // an algebraic check independent of the encoder's own arithmetic.
        bool SyndromeZero(Func<int, int> wordAt, int dataLen, int par0, int par1, int plane)
        {
            var cw = new byte[dataLen + 2];
            for (int j = 0; j < dataLen; j++) cw[j] = sector[12 + 2 * wordAt(j) + plane];
            cw[dataLen] = sector[12 + 2 * par0 + plane];
            cw[dataLen + 1] = sector[12 + 2 * par1 + plane];
            for (int root = 0; root <= 1; root++)
            {
                byte s = 0;
                foreach (byte c in cw) s = (byte)(EdcEcc.GfMul(s, EdcEcc.GfPow(root)) ^ c);
                if (s != 0) return false;
            }
            return true;
        }

        for (int plane = 0; plane < 2; plane++)
        {
            for (int col = 0; col < 43; col++)
            {
                int c = col;
                Assert.True(SyndromeZero(j => c + 43 * j, 24, 1032 + c, 1075 + c, plane),
                    $"P codeword {c} plane {plane}");
            }
            for (int d = 0; d < 26; d++)
            {
                int dd = d;
                Assert.True(SyndromeZero(j => (43 * dd + 44 * j) % 1118, 43,
                        1118 + dd, 1144 + dd, plane),
                    $"Q codeword {dd} plane {plane}");
            }
        }
    }

    // ---- SubQ --------------------------------------------------------------

    [Fact]
    public void PositionFrame_FieldsAndCrc()
    {
        var q = SubQ.Position(QControl.Data, 5, 1, new Msf(0, 10, 30), new Msf(3, 12, 30));
        Assert.Equal(0x41, q[0]);
        Assert.Equal(0x05, q[1]);
        Assert.Equal(0x01, q[2]);
        Assert.Equal(0x10, q[4]);
        Assert.Equal(0x12, q[8]);
        Assert.Equal(Crc16.ComputeInverted(q.AsSpan(0, 10)), (ushort)((q[10] << 8) | q[11]));
    }

    [Fact]
    public void McnFrame_DigitsRoundTrip()
    {
        var mcn = SubQ.Mcn(QControl.None, "5099751223924", new Msf(1, 2, 3));
        Assert.Equal(2, mcn[0] & 0x0F);
        var digits = new StringBuilder();
        for (int d = 0; d < 13; d++)
        {
            int b = mcn[1 + d / 2];
            digits.Append((char)('0' + ((d & 1) == 0 ? b >> 4 : b & 0x0F)));
        }
        Assert.Equal("5099751223924", digits.ToString());
        Assert.Equal(0x03, mcn[9]);
    }

    [Fact]
    public void IsrcFrame_SixBitCharsRoundTrip()
    {
        var q = SubQ.Isrc(QControl.None, "GBAYE0500001", new Msf(0, 4, 22));
        Assert.Equal(3, q[0] & 0x0F);
        int c1 = q[1] >> 2;
        int c2 = ((q[1] & 3) << 4) | (q[2] >> 4);
        int c3 = ((q[2] & 0xF) << 2) | (q[3] >> 6);
        int c4 = q[3] & 0x3F;
        int c5 = q[4] >> 2;
        Assert.Equal("GBAYE", new string(new[]
        {
            (char)(c1 + 0x30), (char)(c2 + 0x30), (char)(c3 + 0x30),
            (char)(c4 + 0x30), (char)(c5 + 0x30),
        }));
    }

    [Fact]
    public void McnValidation_RejectsBadInput()
    {
        Assert.Throws<ArgumentException>(() => SubQ.Mcn(QControl.None, "123", default));
        Assert.Throws<ArgumentException>(() => SubQ.Isrc(QControl.None, "BAD", default));
    }

    // ---- subcode emitters --------------------------------------------------

    [Fact]
    public void PackedAndInterleaved_DescribeTheSameChannels()
    {
        var rnd = new Random(3);
        var f = new SubcodeFrame { P = true };
        rnd.NextBytes(f.Q);
        for (int i = 0; i < 96; i++) f.Rw[i] = (byte)rnd.Next(64);

        var packed = new byte[96];
        var inter = new byte[96];
        f.EmitPacked96(packed);
        f.EmitInterleaved96(inter);

        for (int i = 0; i < 96; i++)
        {
            Assert.Equal((packed[i >> 3] & (0x80 >> (i & 7))) != 0, (inter[i] & 0x80) != 0);
            Assert.Equal((packed[12 + (i >> 3)] & (0x80 >> (i & 7))) != 0, (inter[i] & 0x40) != 0);
            Assert.Equal(f.Rw[i], (byte)(inter[i] & 0x3F));
        }

        var pq = new byte[16];
        f.EmitPq16(pq);
        Assert.True(pq.AsSpan(0, 12).SequenceEqual(f.Q));
        Assert.Equal(0x80, pq[15]);
    }

    // ---- CD-TEXT -----------------------------------------------------------

    [Fact]
    public void CdText_PacksAreWellFormed()
    {
        var text = new CdTextBuilder.DiscText
        {
            AlbumTitle = "Test Album",
            AlbumPerformer = "DiscForge",
            Tracks = new[]
            {
                new CdTextBuilder.TrackText("One", "A"),
                new CdTextBuilder.TrackText("Two", "B"),
            },
        };
        var packs = CdTextBuilder.BuildPacks(text, 1, 2);

        Assert.NotEmpty(packs);
        Assert.All(packs, p => Assert.Equal(18, p.Length));
        Assert.All(packs, p => Assert.Equal(
            Crc16.ComputeInverted(p.AsSpan(0, 16)), (ushort)((p[16] << 8) | p[17])));
        Assert.Equal(0x80, packs[0][0]);
        Assert.Equal(0, packs[0][1]);
        Assert.Equal(3, packs.Count(p => p[0] == 0x8F));
        for (int i = 0; i < packs.Length; i++) Assert.Equal(i, packs[i][2]);

        var rw = new byte[96];
        CdTextBuilder.FillSectorRw(packs, 0, rw);
        Assert.All(rw, s => Assert.True(s < 64));
        Assert.Equal(0x20, rw[0]);   // top 6 bits of pack type 0x80
    }

    [Fact]
    public void CdText_EmptyMeansNoPacks()
    {
        Assert.Empty(CdTextBuilder.BuildPacks(new CdTextBuilder.DiscText(), 1, 1));
    }

    // ---- CUE ---------------------------------------------------------------

    [Fact]
    public void Cue_ParsesFullSemantics_AndRoundTrips()
    {
        const string cue = """
            CATALOG 5099751223924
            TITLE "Album X"
            PERFORMER "Artist Y"
            FILE "disc.bin" BINARY
              TRACK 01 AUDIO
                TITLE "Song 1"
                FLAGS DCP PRE
                ISRC GBAYE0500001
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                PREGAP 00:03:00
                INDEX 00 02:00:00
                INDEX 01 02:02:00
                INDEX 02 02:30:00
                POSTGAP 00:01:00
            """;
        var sheet = CueSheet.Parse(cue);

        Assert.Equal("5099751223924", sheet.Catalog);
        Assert.Equal("Album X", sheet.Title);
        Assert.Equal(CueFlags.Dcp | CueFlags.PreEmphasis, sheet.Tracks[0].Flags);
        Assert.Equal("GBAYE0500001", sheet.Tracks[0].Isrc);
        Assert.Equal("Song 1", sheet.Tracks[0].Title);
        Assert.Equal(225, sheet.Tracks[1].Pregap?.ToSectors());
        Assert.Equal(75, sheet.Tracks[1].Postgap?.ToSectors());
        Assert.Equal(3, sheet.Tracks[1].Indices.Count);

        var round = CueSheet.Parse(sheet.Write());
        Assert.Equal(sheet.Catalog, round.Catalog);
        Assert.Equal(sheet.Tracks[0].Flags, round.Tracks[0].Flags);
        Assert.Equal(3, round.Tracks[1].Indices.Count);
    }

    // ---- generator ---------------------------------------------------------

    private static (DiscLayout layout, MemoryStream img, byte[] pcm) GenerateAudioDisc()
    {
        var pcm = new byte[(130 + 4 + 8) * 2352];
        new Random(11).NextBytes(pcm);
        var bin = new MemoryStream(pcm);
        const string cueText = """
            CATALOG 1234567890123
            TITLE "T"
            FILE "x.bin" BINARY
              TRACK 01 AUDIO
                TITLE "A"
                ISRC GBAYE0500001
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                INDEX 00 00:01:55
                INDEX 01 00:01:59
                INDEX 02 00:01:63
            """;
        var layout = DiscLayout.FromCue(CueSheet.Parse(cueText), _ => bin);
        var img = new MemoryStream();
        RawImageGenerator.Generate(layout, RawSubcodeForm.Pq16, img);
        return (layout, img, pcm);
    }

    private static byte[] SectorQ(MemoryStream img, long sector, int size)
    {
        var q = new byte[12];
        img.Position = sector * size + 2352;
        img.ReadExactly(q, 0, 12);
        return q;
    }

    [Fact]
    public void Generator_AudioDisc_LayoutAndSubcode()
    {
        var (layout, img, pcm) = GenerateAudioDisc();
        using var _ = layout;

        Assert.Equal(150, layout.Tracks[0].PregapGeneratedSectors);
        Assert.Equal(4, layout.Tracks[1].PregapStoredSectors);
        Assert.Equal(150 + 130 + 4 + 8, RawImageGenerator.ProgramSectors(layout));
        Assert.Equal(RawImageGenerator.TotalSectors(layout) * 2368, img.Length);

        // Lead-in TOC.
        var a0 = SectorQ(img, 0, 2368);
        Assert.Equal(0xA0, a0[2]);
        Assert.Equal(0x95, a0[3]);
        Assert.Equal(0x01, a0[7]);
        var a2 = SectorQ(img, 6, 2368);
        Assert.Equal(0xA2, a2[2]);
        Assert.Equal(0x03, a2[8]);   // lead-out at 292 sectors = 00:03:67
        Assert.Equal(0x67, a2[9]);
        var tr1 = SectorQ(img, 9, 2368);
        Assert.Equal(0x01, tr1[2]);
        Assert.Equal(0x02, tr1[8]);  // track 1 index 1 at 00:02:00

        long p0 = RawImageGenerator.LeadInSectors;

        // Track 1 pregap countdown and index start.
        var first = SectorQ(img, p0, 2368);
        Assert.Equal(0x01, first[1]);
        Assert.Equal(0x00, first[2]);
        Assert.Equal(0x02, first[4]);        // countdown from 150 = 00:02:00
        Assert.Equal(0x01, SectorQ(img, p0 + 149, 2368)[5]);
        var idx1 = SectorQ(img, p0 + 150, 2368);
        Assert.Equal(0x01, idx1[2]);
        Assert.Equal(0, idx1[3] | idx1[4] | idx1[5]);

        // P flag transitions.
        img.Position = p0 * 2368 + 2352 + 15;
        Assert.Equal(0x80, img.ReadByte());
        img.Position = (p0 + 150) * 2368 + 2352 + 15;
        Assert.Equal(0x00, img.ReadByte());

        // Track 2: countdown, exact INDEX 02 placement, relative continuity.
        var t2p = SectorQ(img, p0 + 280, 2368);
        Assert.Equal(0x02, t2p[1]);
        Assert.Equal(0x00, t2p[2]);
        Assert.Equal(0x04, t2p[5]);
        var t2i2 = SectorQ(img, p0 + 288, 2368);
        Assert.Equal(0x02, t2i2[2]);
        Assert.Equal(0x04, t2i2[5]);

        // MCN / ISRC cadence and the 9-in-10 position-frame rule.
        Assert.Equal(2, SectorQ(img, p0 + 248, 2368)[0] & 0x0F);
        Assert.Equal(3, SectorQ(img, p0 + 198, 2368)[0] & 0x0F);
        int special = 0;
        for (long s = p0 + 190; s < p0 + 200; s++)
            special += (SectorQ(img, s, 2368)[0] & 0x0F) != 1 ? 1 : 0;
        Assert.Equal(1, special);

        // Audio main channel is passed through verbatim.
        img.Position = (p0 + 150) * 2368;
        var mainOut = new byte[2352];
        img.ReadExactly(mainOut, 0, 2352);
        Assert.True(mainOut.AsSpan().SequenceEqual(pcm.AsSpan(0, 2352)));
    }

    [Fact]
    public void Generator_DataDisc_SynthesisesAndScrambles()
    {
        var user = new byte[6 * 2048];
        new Random(5).NextBytes(user);
        using var bin = new MemoryStream(user);
        const string cueText = """
            FILE "d.bin" BINARY
              TRACK 01 MODE1/2048
                INDEX 01 00:00:00
            """;
        using var layout = DiscLayout.FromCue(CueSheet.Parse(cueText), _ => bin);
        using var img = new MemoryStream();
        RawImageGenerator.Generate(layout, RawSubcodeForm.Packed96, img);

        Assert.Equal((RawImageGenerator.LeadInSectors + 150 + 6) * 2448L, img.Length);

        long p1 = (RawImageGenerator.LeadInSectors + 150) * 2448L;
        img.Position = p1;
        var main = new byte[2352];
        img.ReadExactly(main, 0, 2352);
        CdScrambler.ScrambleInPlace(main);   // descramble

        Assert.Equal(0xFF, main[1]);
        Assert.Equal(1, main[15]);
        Assert.Equal(0x02, main[13]);        // header at 00:02:00
        Assert.True(main.AsSpan(16, 2048).SequenceEqual(user.AsSpan(0, 2048)));
        uint edc = EdcEcc.ComputeEdc(main.AsSpan(0, 2064));
        Assert.Equal((byte)edc, main[2064]);

        // Packed subcode: Q sits at bytes 12..23 and carries the data bit.
        img.Position = p1 + 2352 + 12;
        var q = new byte[12];
        img.ReadExactly(q, 0, 12);
        Assert.Equal(0x41, q[0]);
    }

    [Fact]
    public void Generator_CdText_RidesTheLeadIn()
    {
        var pcm = new byte[10 * 2352];
        using var bin = new MemoryStream(pcm);
        const string cueText = """
            TITLE "Album"
            FILE "x.bin" BINARY
              TRACK 01 AUDIO
                TITLE "Song"
                INDEX 01 00:00:00
            """;
        using var layout = DiscLayout.FromCue(CueSheet.Parse(cueText), _ => bin);
        using var img = new MemoryStream();
        RawImageGenerator.Generate(layout, RawSubcodeForm.Packed96, img);

        // Lead-in sector 0, packed layout: R starts at subcode byte 24. The
        // first pack byte is type 0x80, whose top bit lands in R's first bit.
        img.Position = 2352 + 24;
        Assert.Equal(0x80, img.ReadByte() & 0x80);

        // The R-W planes of the lead-in are not all zero (packs present)…
        img.Position = 2352 + 24;
        var rw = new byte[72];
        img.ReadExactly(rw, 0, 72);
        Assert.Contains(rw, b => b != 0);

        // …but the program area's are.
        img.Position = (RawImageGenerator.LeadInSectors + 150) * 2448L + 2352 + 24;
        img.ReadExactly(rw, 0, 72);
        Assert.All(rw, b => Assert.Equal(0, b));
    }
}
