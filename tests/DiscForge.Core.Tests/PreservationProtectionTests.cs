// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Forensics;
using DiscForge.Core.Preservation;
using Xunit;

namespace DiscForge.Core.Tests;

public class PreservationProtectionTests
{
    private static PreservationManifest Manifest()
    {
        var m = new PreservationManifest { Generator = "test" };
        m.Entries.Add(new PreservationEntry
        {
            Path = "game.cue", Length = 42, Crc32 = "deadbeef",
            Md5 = "0", Sha1 = "0", Sha256 = "0",
        });
        m.Digest = PreservationPackage.ComputeDigest(m);
        return m;
    }

    private static FusedProtection Fused() => new()
    {
        Standing = ProtectionStanding.Corroborated,
        Schemes = new[] { "LibCrypt" },
        PhysicalSignature = true,
        Evidence = new[] { "subchannel: 16 paired invalid-Q sectors", "filesystem: none" },
        Guidance = "preserve the subchannel verbatim",
    };

    [Fact]
    public void Records_the_protection_verdict_as_provenance()
    {
        var m = Manifest();
        PreservationPackage.SetProtection(m, Fused());

        Assert.NotNull(m.Protection);
        Assert.Equal("Corroborated", m.Protection!.Standing);
        Assert.Contains("LibCrypt", m.Protection.Schemes);
        Assert.True(m.Protection.PhysicalSignature);
        Assert.Equal("preserve the subchannel verbatim", m.Protection.Guidance);
    }

    [Fact]
    public void The_digest_stays_valid_after_recording_protection()
    {
        var m = Manifest();
        string before = m.Digest!;
        PreservationPackage.SetProtection(m, Fused());

        Assert.NotEqual(before, m.Digest);              // the addition changed the manifest
        Assert.True(PreservationPackage.DigestValid(m)); // and the digest was refreshed to match
    }

    [Fact]
    public void Tampering_with_the_protection_record_breaks_the_digest()
    {
        var m = Manifest();
        PreservationPackage.SetProtection(m, Fused());
        m.Protection!.Standing = "None";                // silently downgrade the verdict
        Assert.False(PreservationPackage.DigestValid(m));
    }

    [Fact]
    public void Protection_round_trips_through_json()
    {
        var m = Manifest();
        PreservationPackage.SetProtection(m, Fused());
        var round = PreservationPackage.FromJson(PreservationPackage.ToJson(m));

        Assert.NotNull(round.Protection);
        Assert.Equal("Corroborated", round.Protection!.Standing);
        Assert.Contains("LibCrypt", round.Protection.Schemes);
        Assert.True(PreservationPackage.DigestValid(round));
    }

    [Fact]
    public void A_clean_disc_records_a_none_standing()
    {
        var m = Manifest();
        PreservationPackage.SetProtection(m, new FusedProtection
        {
            Standing = ProtectionStanding.None,
            Schemes = Array.Empty<string>(),
            PhysicalSignature = false,
            Evidence = Array.Empty<string>(),
            Guidance = "no protection detected",
        });
        Assert.Equal("None", m.Protection!.Standing);
        Assert.True(PreservationPackage.DigestValid(m));
    }
}
