using DiscForge.Core.Forensics;
using DiscForge.Core.Preservation;
using Xunit;

namespace DiscForge.Core.Tests;

public class ConsensusLedgerTests
{
    private static GenomeFingerprint Genome(string layout, string data, byte[]? audio = null) => new()
    {
        LayoutHash = layout,
        DataHash = data,
        AudioEnvelope = audio ?? new byte[] { 1, 2, 3, 4 },
        AudioTrackCount = 1,
    };

    private static ConsensusAttestation Attest(string discId, GenomeFingerprint g, string utc = "2026-01-01T00:00:00Z")
    {
        var (priv, _) = ConsensusLog.GenerateKey();
        using var key = ConsensusLog.LoadPrivateKey(priv);
        return ConsensusLog.CreateAttestation(discId, g, key, utc);
    }

    [Fact]
    public void An_attestation_verifies_and_tampering_breaks_it()
    {
        var a = Attest("Game A", Genome("L1", "D1"));
        Assert.True(ConsensusLog.VerifyAttestation(a));

        a.DataHash = "D2";                       // tamper with the claimed identity
        Assert.False(ConsensusLog.VerifyAttestation(a));
    }

    [Fact]
    public void The_ledger_chain_detects_removal()
    {
        var ledger = ConsensusLog.NewLedger();
        ConsensusLog.Append(ledger, Attest("A", Genome("L", "D")));
        ConsensusLog.Append(ledger, Attest("A", Genome("L", "D")));
        ConsensusLog.Append(ledger, Attest("A", Genome("L", "D")));
        Assert.True(ConsensusLog.VerifyLedger(ledger));

        ledger.Attestations.RemoveAt(1);         // splice one out
        Assert.False(ConsensusLog.VerifyLedger(ledger));
    }

    [Fact]
    public void Two_independent_dumpers_corroborate_three_reach_consensus()
    {
        var ledger = ConsensusLog.NewLedger();
        ConsensusLog.Append(ledger, Attest("Ridge Racer", Genome("L", "D")));
        ConsensusLog.Append(ledger, Attest("Ridge Racer", Genome("L", "D")));

        var two = ConsensusLog.Tally(ledger).Single();
        Assert.Equal(2, two.IndependentDumpers);
        Assert.Equal(ConsensusLevel.Corroborated, two.Level);

        ConsensusLog.Append(ledger, Attest("Ridge Racer", Genome("L", "D")));
        var three = ConsensusLog.Tally(ledger).Single();
        Assert.Equal(ConsensusLevel.Consensus, three.Level);
        Assert.Equal(3, three.IndependentDumpers);
    }

    [Fact]
    public void The_same_key_attesting_twice_counts_once()
    {
        var (priv, _) = ConsensusLog.GenerateKey();
        using var key = ConsensusLog.LoadPrivateKey(priv);
        var g = Genome("L", "D");

        var ledger = ConsensusLog.NewLedger();
        ConsensusLog.Append(ledger, ConsensusLog.CreateAttestation("A", g, key, "t1"));
        ConsensusLog.Append(ledger, ConsensusLog.CreateAttestation("A", g, key, "t2"));

        var r = ConsensusLog.Tally(ledger).Single();
        Assert.Equal(1, r.IndependentDumpers);      // distinct keys, not attestations
        Assert.Equal(2, r.Attestations);
        Assert.Equal(ConsensusLevel.Single, r.Level);
    }

    [Fact]
    public void Different_read_offsets_still_reach_the_same_identity()
    {
        // Same layout+data (offset-invariant), different audio envelopes (offset-variant): one identity.
        var a = Attest("Wipeout", Genome("L", "D", new byte[] { 10, 11, 12 }));
        var b = Attest("Wipeout", Genome("L", "D", new byte[] { 40, 41, 42 }));
        Assert.Equal(a.GenomeKey, b.GenomeKey);

        var ledger = ConsensusLog.NewLedger();
        ConsensusLog.Append(ledger, a);
        ConsensusLog.Append(ledger, b);
        var r = ConsensusLog.Tally(ledger).Single();
        Assert.Equal(2, r.IndependentDumpers);
    }

    [Fact]
    public void Two_images_claiming_the_same_title_are_flagged_disputed()
    {
        var ledger = ConsensusLog.NewLedger();
        ConsensusLog.Append(ledger, Attest("Some Game", Genome("L", "D1")));
        ConsensusLog.Append(ledger, Attest("Some Game", Genome("L", "D2")));   // divergent data

        var results = ConsensusLog.Tally(ledger);
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Disputed));
    }

    [Fact]
    public void A_ledger_survives_a_json_round_trip()
    {
        var ledger = ConsensusLog.NewLedger();
        ConsensusLog.Append(ledger, Attest("A", Genome("L", "D")));
        ConsensusLog.Append(ledger, Attest("A", Genome("L", "D")));

        var back = ConsensusLog.FromJson(ConsensusLog.ToJson(ledger));
        Assert.True(ConsensusLog.VerifyLedger(back));
        Assert.Equal(2, ConsensusLog.Tally(back).Single().IndependentDumpers);
    }
}
