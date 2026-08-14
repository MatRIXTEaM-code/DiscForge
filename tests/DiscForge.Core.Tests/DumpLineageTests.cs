using DiscForge.Core.Preservation;
using Xunit;

namespace DiscForge.Core.Tests;

public class DumpLineageTests
{
    private static DumpLineage SampleLineage()
    {
        var lin = DumpLineageLog.Start("CD Extra album", "dumped",
            actor: "TSSTcorp CDDVDW SE-208DB (TS02)",
            detail: "raw CD read, jitter-corrected",
            utc: "2026-07-28T16:35:00Z",
            data: new Dictionary<string, string> { ["imageCrc32"] = "28c1ff6d" });
        DumpLineageLog.Append(lin, "dumped",
            actor: "LITE-ON DVDRW SHW-160P6S (P901)",
            detail: "second drive",
            utc: "2026-07-28T18:44:00Z",
            data: new Dictionary<string, string> { ["imageCrc32"] = "28c1ff6d" });
        DumpLineageLog.Append(lin, "corroborated",
            actor: "DiscForge",
            detail: "both drives agree on all 10 track CRC-32s");
        DumpLineageLog.Append(lin, "sealed",
            actor: "DiscForge",
            detail: "preservation manifest built",
            data: new Dictionary<string, string> { ["digest"] = "dbdde3874f6b" });
        return lin;
    }

    [Fact]
    public void A_fresh_lineage_chain_verifies()
    {
        var lin = SampleLineage();

        Assert.Equal(4, lin.Events.Count);
        Assert.Equal(0, lin.Events[0].Seq);
        Assert.Equal("", lin.Events[0].PrevHash);
        Assert.Equal(lin.Events[2].Hash, lin.Events[3].PrevHash);   // each links to the last
        Assert.True(DumpLineageLog.VerifyChain(lin));
    }

    [Fact]
    public void Editing_an_event_breaks_the_chain()
    {
        var lin = SampleLineage();
        lin.Events[1].Detail = "tampered";

        Assert.False(DumpLineageLog.VerifyChain(lin));
    }

    [Fact]
    public void Removing_or_reordering_events_breaks_the_chain()
    {
        var lin = SampleLineage();
        (lin.Events[1], lin.Events[2]) = (lin.Events[2], lin.Events[1]);   // swap two events

        Assert.False(DumpLineageLog.VerifyChain(lin));
    }

    [Fact]
    public void A_signed_lineage_verifies_and_survives_json()
    {
        var lin = SampleLineage();
        var (priv, _) = DumpLineageLog.GenerateKey();
        using (var key = DumpLineageLog.LoadPrivateKey(priv))
            DumpLineageLog.Sign(lin, key);

        Assert.True(lin.Signed);
        Assert.True(DumpLineageLog.VerifySignature(lin));

        var back = DumpLineageLog.FromJson(DumpLineageLog.ToJson(lin));
        Assert.True(DumpLineageLog.VerifyChain(back));
        Assert.True(DumpLineageLog.VerifySignature(back));   // public key travels with it
    }

    [Fact]
    public void Tampering_after_signing_fails_signature_verification()
    {
        var lin = SampleLineage();
        using (var key = DumpLineageLog.LoadPrivateKey(DumpLineageLog.GenerateKey().PrivateKeyBase64))
            DumpLineageLog.Sign(lin, key);
        Assert.True(DumpLineageLog.VerifySignature(lin));

        lin.Events[0].Detail = "history rewritten";

        Assert.False(DumpLineageLog.VerifyChain(lin));
        Assert.False(DumpLineageLog.VerifySignature(lin));
    }

    [Fact]
    public void A_signature_from_a_different_key_does_not_verify()
    {
        var lin = SampleLineage();
        using (var a = DumpLineageLog.LoadPrivateKey(DumpLineageLog.GenerateKey().PrivateKeyBase64))
            DumpLineageLog.Sign(lin, a);

        // Swap in a different public key: the signature no longer matches it.
        var (_, otherPub) = DumpLineageLog.GenerateKey();
        lin.PublicKey = otherPub;

        Assert.False(DumpLineageLog.VerifySignature(lin));
    }

    [Fact]
    public void Appending_after_signing_clears_the_signature()
    {
        var lin = SampleLineage();
        using (var key = DumpLineageLog.LoadPrivateKey(DumpLineageLog.GenerateKey().PrivateKeyBase64))
            DumpLineageLog.Sign(lin, key);
        Assert.True(lin.Signed);

        DumpLineageLog.Append(lin, "verified", actor: "DiscForge", detail: "re-checked");

        Assert.False(lin.Signed);                       // the head moved; old signature is dropped
        Assert.True(DumpLineageLog.VerifyChain(lin));   // chain still intact, just unsigned now
    }
}
