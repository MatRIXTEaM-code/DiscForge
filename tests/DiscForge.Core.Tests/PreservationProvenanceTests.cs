using DiscForge.Core.Preservation;
using Xunit;

namespace DiscForge.Core.Tests;

public class PreservationProvenanceTests
{
    private static PreservationManifest Manifest()
    {
        var m = new PreservationManifest { Generator = "test" };
        m.Entries.Add(new PreservationEntry
        {
            Path = "cdextra.cdi",
            Length = 100,
            Crc32 = "28c1ff6d",
            Md5 = "0b7e31e7ca3faaff8e03ec599441633e",
            Sha1 = "ffb982d0a5defdcf05568188b0ef70aae060c2e5",
            Sha256 = "00",
        });
        m.Digest = PreservationPackage.ComputeDigest(m);
        return m;
    }

    [Fact]
    public void Two_matching_drives_corroborate_the_dump()
    {
        var m = Manifest();

        bool a = PreservationPackage.AddAttestation(m, new DumpAttestation
        {
            Drive = "TSSTcorp CDDVDW SE-208DB",
            TrackCrc32 = new() { "cf5feeb0", "c7234b93", "3d027eeb" },
            ImageCrc32 = "28c1ff6d",
        });
        Assert.True(a);                                  // first establishes the reference and agrees
        Assert.False(m.Provenance!.Corroborated);        // one source is not yet corroboration

        // Second drive, same disc — note the CRCs are given upper-case to prove normalisation.
        bool b = PreservationPackage.AddAttestation(m, new DumpAttestation
        {
            Drive = "LITE-ON DVDRW SHW-160P6S (P901)",
            TrackCrc32 = new() { "CF5FEEB0", "C7234B93", "3D027EEB" },
            ImageCrc32 = "28C1FF6D",
        });

        Assert.True(b);
        Assert.True(m.Provenance!.Corroborated);
        Assert.Equal(2, m.Provenance.IndependentAgreements);
        Assert.True(PreservationPackage.DigestValid(m));  // digest was refreshed by AddAttestation
    }

    [Fact]
    public void A_disagreeing_drive_breaks_corroboration()
    {
        var m = Manifest();
        PreservationPackage.AddAttestation(m, new DumpAttestation
        {
            Drive = "A", TrackCrc32 = new() { "cf5feeb0", "c7234b93", "3d027eeb" },
        });
        bool c = PreservationPackage.AddAttestation(m, new DumpAttestation
        {
            Drive = "B", TrackCrc32 = new() { "cf5feeb0", "c7234b93", "deadbeef" },   // track 3 differs
        });

        Assert.False(c);
        Assert.False(m.Provenance!.Corroborated);
        Assert.Equal(1, m.Provenance.IndependentAgreements);
    }

    [Fact]
    public void Provenance_survives_json_and_keeps_its_digest()
    {
        var m = Manifest();
        PreservationPackage.AddAttestation(m, new DumpAttestation
        {
            Drive = "A", ImageCrc32 = "28c1ff6d", TrackCrc32 = new() { "cf5feeb0", "3d027eeb" },
        });
        PreservationPackage.AddAttestation(m, new DumpAttestation
        {
            Drive = "B", ImageCrc32 = "28c1ff6d", TrackCrc32 = new() { "cf5feeb0", "3d027eeb" },
        });

        string json = PreservationPackage.ToJson(m);
        var back = PreservationPackage.FromJson(json);

        Assert.True(PreservationPackage.DigestValid(back));
        Assert.NotNull(back.Provenance);
        Assert.True(back.Provenance!.Corroborated);
        Assert.Equal(2, back.Provenance.Attestations.Count);
        Assert.Equal("28c1ff6d", back.Provenance.ReferenceImageCrc32);
    }

    [Fact]
    public void Editing_an_attestation_after_sealing_is_caught_by_the_digest()
    {
        var m = Manifest();
        PreservationPackage.AddAttestation(m, new DumpAttestation
        {
            Drive = "A", TrackCrc32 = new() { "cf5feeb0", "3d027eeb" },
        });
        Assert.True(PreservationPackage.DigestValid(m));

        // Tamper with the recorded provenance without refreshing the digest.
        m.Provenance!.Attestations[0].Drive = "Someone Else's Drive";

        Assert.False(PreservationPackage.DigestValid(m));
    }
}
