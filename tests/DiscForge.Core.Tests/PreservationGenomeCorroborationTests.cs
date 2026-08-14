using DiscForge.Core.Forensics;
using DiscForge.Core.Preservation;
using Xunit;

namespace DiscForge.Core.Tests;

public class PreservationGenomeCorroborationTests
{
    private static short Signal(int frame)
    {
        double amp = 0.5 + 0.5 * Math.Sin(2 * Math.PI * frame / 9000.0);
        return (short)(amp * 28000 * Math.Sin(2 * Math.PI * frame / 32.0));
    }

    // Audio track windowed at a given physical frame — the read-offset simulation.
    private static byte[] AudioWindow(int startFrame, int sectors)
    {
        var b = new byte[sectors * 2352];
        int frames = sectors * 588;
        for (int f = 0; f < frames; f++)
        {
            short v = Signal(startFrame + f);
            int o = f * 4;
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)v; b[o + 3] = (byte)(v >> 8);
        }
        return b;
    }

    // Raw data track: sync-bearing Mode 1 sectors with deterministic content.
    private static byte[] DataTrack(int seed, int sectors)
    {
        var b = new byte[sectors * 2352];
        var rng = new Random(seed);
        for (int s = 0; s < sectors; s++)
        {
            int o = s * 2352;
            b[o] = 0x00;
            for (int i = 1; i <= 10; i++) b[o + i] = 0xFF;
            b[o + 11] = 0x00; b[o + 15] = 0x01;
            for (int i = 16; i < 2352; i++) b[o + i] = (byte)rng.Next(256);
        }
        return b;
    }

    private static GenomeFingerprint Genome(int dataSeed, int audioStartFrame) =>
        DiscGenome.Compute(new List<GenomeTrack>
        {
            // A realistic data-track size (well above the transition guard), since it is
            // followed by audio and the genome trims a guard from the data track's end.
            new(1, true, DataTrack(dataSeed, 500)),
            new(2, false, AudioWindow(audioStartFrame, 100)),
        });

    private static PreservationManifest Manifest()
    {
        var m = new PreservationManifest { Generator = "test" };
        m.Entries.Add(new PreservationEntry { Path = "game.cdi", Length = 1, Crc32 = "0", Md5 = "0", Sha1 = "0", Sha256 = "0" });
        m.Digest = PreservationPackage.ComputeDigest(m);
        return m;
    }

    [Fact]
    public void Two_drives_at_different_offsets_corroborate_by_genome()
    {
        var m = Manifest();
        var liteon = Genome(dataSeed: 1, audioStartFrame: 1000);
        var tsst = Genome(dataSeed: 1, audioStartFrame: 1030);   // same disc, 30-frame read-offset gap

        var match = PreservationPackage.AddGenomeCorroboration(m, "LITE-ON SHW-160P6S", liteon, "TSSTcorp SE-208DB", tsst);

        Assert.True(match.SameDisc);                    // raw CRCs would differ; the genome agrees
        Assert.True(m.Provenance!.Corroborated);
        Assert.Equal(2, m.Provenance.Attestations.Count);
        Assert.All(m.Provenance.Attestations, a => Assert.Equal("genome", a.Basis));
        Assert.True(PreservationPackage.DigestValid(m));   // digest refreshed and intact
    }

    [Fact]
    public void The_other_drives_attestation_records_the_offset_and_similarity()
    {
        var m = Manifest();
        PreservationPackage.AddGenomeCorroboration(m, "A", Genome(1, 1000), "B", Genome(1, 1030));

        var other = m.Provenance!.Attestations[1];
        Assert.Equal("B", other.Drive);
        Assert.True(other.Agrees);
        Assert.NotNull(other.AudioSimilarity);
        Assert.True(other.AudioSimilarity > 0.9);
        Assert.NotNull(other.OffsetShift);
    }

    [Fact]
    public void A_different_disc_is_not_genome_corroborated()
    {
        var m = Manifest();
        var a = Genome(dataSeed: 1, audioStartFrame: 1000);
        var b = Genome(dataSeed: 2, audioStartFrame: 1000);   // different addressed data

        var match = PreservationPackage.AddGenomeCorroboration(m, "A", a, "B", b);

        Assert.False(match.SameDisc);
        Assert.False(m.Provenance!.Corroborated);
        Assert.False(m.Provenance.Attestations[1].Agrees);
    }

    [Fact]
    public void Genome_provenance_survives_json_round_trip()
    {
        var m = Manifest();
        PreservationPackage.AddGenomeCorroboration(m, "A", Genome(1, 1000), "B", Genome(1, 1030));

        var back = PreservationPackage.FromJson(PreservationPackage.ToJson(m));

        Assert.True(PreservationPackage.DigestValid(back));
        Assert.True(back.Provenance!.Corroborated);
        Assert.Equal("genome", back.Provenance.Attestations[1].Basis);
    }
}
