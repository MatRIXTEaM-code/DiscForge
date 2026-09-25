// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Provenance;
using Xunit;

namespace DiscForge.Core.Tests;

public class DumpCertificateLedgerTests
{
    private static LedgerEntry Submit(DumpCertificateLedger ledger, string fingerprint, string outputSha256,
        string? utc = null)
    {
        using var key = DumpCertificateLedgerLog.LoadPrivateKey(DumpCertificateLedgerLog.GenerateKey().PrivateKeyBase64);
        var claim = DumpCertificateLedgerLog.CreateSubmission(fingerprint, outputSha256, key, utc: utc ?? "2026-01-01T00:00:00Z");
        return DumpCertificateLedgerLog.Append(ledger, claim);
    }

    [Fact]
    public void A_fresh_ledger_has_no_head_and_an_empty_chain_verifies()
    {
        var ledger = new DumpCertificateLedger();
        Assert.Null(ledger.HeadHash);
        Assert.True(DumpCertificateLedgerLog.VerifyChain(ledger));
        Assert.True(DumpCertificateLedgerLog.VerifyAllSubmitterSignatures(ledger));
    }

    [Fact]
    public void Appending_links_each_entry_to_the_previous_head()
    {
        var ledger = new DumpCertificateLedger();
        var e0 = Submit(ledger, "disc-a", "aa11");
        var e1 = Submit(ledger, "disc-a", "aa11");

        Assert.Equal("", e0.PrevHash);
        Assert.Equal(e0.Hash, e1.PrevHash);
        Assert.Equal(0, e0.Seq);
        Assert.Equal(1, e1.Seq);
        Assert.True(DumpCertificateLedgerLog.VerifyChain(ledger));
    }

    [Fact]
    public void A_round_tripped_ledger_still_verifies()
    {
        var ledger = new DumpCertificateLedger();
        Submit(ledger, "disc-a", "aa11");
        Submit(ledger, "disc-b", "bb22");

        string json = DumpCertificateLedgerLog.ToJson(ledger);
        var reloaded = DumpCertificateLedgerLog.FromJson(json);

        Assert.True(DumpCertificateLedgerLog.VerifyChain(reloaded));
        Assert.True(DumpCertificateLedgerLog.VerifyAllSubmitterSignatures(reloaded));
        Assert.Equal(ledger.HeadHash, reloaded.HeadHash);
    }

    [Fact]
    public void Editing_a_past_entrys_claim_breaks_the_chain()
    {
        var ledger = new DumpCertificateLedger();
        Submit(ledger, "disc-a", "aa11");
        Submit(ledger, "disc-a", "aa11");

        var tampered = ledger.Entries[0] with { OutputSha256 = "ffff" };
        ledger.Entries[0] = tampered;

        Assert.False(DumpCertificateLedgerLog.VerifyChain(ledger));
    }

    [Fact]
    public void Removing_an_entry_breaks_the_chain_for_everything_after_it()
    {
        var ledger = new DumpCertificateLedger();
        Submit(ledger, "disc-a", "aa11");
        Submit(ledger, "disc-a", "aa11");
        Submit(ledger, "disc-a", "aa11");

        ledger.Entries.RemoveAt(1);   // splice out the middle entry without fixing up Seq/PrevHash

        Assert.False(DumpCertificateLedgerLog.VerifyChain(ledger));
    }

    [Fact]
    public void Append_refuses_a_submission_with_a_forged_signature()
    {
        var ledger = new DumpCertificateLedger();
        using var key = DumpCertificateLedgerLog.LoadPrivateKey(DumpCertificateLedgerLog.GenerateKey().PrivateKeyBase64);
        var claim = DumpCertificateLedgerLog.CreateSubmission("disc-a", "aa11", key, utc: "2026-01-01T00:00:00Z");
        var forged = claim with { OutputSha256 = "ffffffff" };   // claim content changed after signing

        Assert.False(DumpCertificateLedgerLog.VerifySubmitterSignature(forged));
        Assert.Throws<InvalidOperationException>(() => DumpCertificateLedgerLog.Append(ledger, forged));
        Assert.Empty(ledger.Entries);
    }

    [Fact]
    public void A_submitters_signature_is_portable_across_ledgers()
    {
        using var key = DumpCertificateLedgerLog.LoadPrivateKey(DumpCertificateLedgerLog.GenerateKey().PrivateKeyBase64);
        var claim = DumpCertificateLedgerLog.CreateSubmission("disc-a", "aa11", key, utc: "2026-01-01T00:00:00Z");

        var ledgerOne = new DumpCertificateLedger();
        var ledgerTwo = new DumpCertificateLedger();
        Submit(ledgerTwo, "disc-z", "zz99");   // give ledgerTwo a different head first

        var inOne = DumpCertificateLedgerLog.Append(ledgerOne, claim);
        var inTwo = DumpCertificateLedgerLog.Append(ledgerTwo, claim);

        // Same claim, different chain position -> different Seq/PrevHash/Hash, but both verify independently.
        Assert.NotEqual(inOne.Hash, inTwo.Hash);
        Assert.True(DumpCertificateLedgerLog.VerifyChain(ledgerOne));
        Assert.True(DumpCertificateLedgerLog.VerifyChain(ledgerTwo));
        Assert.True(DumpCertificateLedgerLog.VerifySubmitterSignature(inOne));
        Assert.True(DumpCertificateLedgerLog.VerifySubmitterSignature(inTwo));
    }

    [Fact]
    public void Independent_submitters_agreeing_on_the_same_bytes_are_not_disputed()
    {
        var ledger = new DumpCertificateLedger();
        Submit(ledger, "disc-a", "same-hash");
        Submit(ledger, "disc-a", "same-hash");
        Submit(ledger, "disc-a", "same-hash");

        var consensus = DumpCertificateLedgerLog.Consensus(ledger, "disc-a");

        Assert.Equal(3, consensus.TotalSubmissions);
        Assert.False(consensus.Disputed);
        Assert.NotNull(consensus.Leading);
        Assert.Equal(3, consensus.Leading!.SubmitterCount);
    }

    [Fact]
    public void Disagreeing_submissions_are_flagged_disputed()
    {
        var ledger = new DumpCertificateLedger();
        Submit(ledger, "disc-a", "hash-one");
        Submit(ledger, "disc-a", "hash-one");
        Submit(ledger, "disc-a", "hash-two");

        var consensus = DumpCertificateLedgerLog.Consensus(ledger, "disc-a");

        Assert.True(consensus.Disputed);
        Assert.Equal(2, consensus.Groups.Count);
        Assert.Equal("hash-one", consensus.Leading!.OutputSha256);
        Assert.Equal(2, consensus.Leading!.SubmitterCount);
    }

    [Fact]
    public void The_same_submitter_re_attesting_never_inflates_the_consensus_count()
    {
        var ledger = new DumpCertificateLedger();
        using var key = DumpCertificateLedgerLog.LoadPrivateKey(DumpCertificateLedgerLog.GenerateKey().PrivateKeyBase64);

        // Same key, two separate (but identically-signed) submissions for the same claim.
        var claim1 = DumpCertificateLedgerLog.CreateSubmission("disc-a", "same-hash", key, utc: "2026-01-01T00:00:00Z");
        var claim2 = DumpCertificateLedgerLog.CreateSubmission("disc-a", "same-hash", key, utc: "2026-01-01T00:00:00Z");
        DumpCertificateLedgerLog.Append(ledger, claim1);
        DumpCertificateLedgerLog.Append(ledger, claim2);

        var consensus = DumpCertificateLedgerLog.Consensus(ledger, "disc-a");

        Assert.Equal(2, consensus.TotalSubmissions);      // two entries...
        Assert.Equal(1, consensus.Leading!.SubmitterCount); // ...but one distinct submitter.
    }

    [Fact]
    public void GetSubmitterSigningBytes_is_exactly_what_an_alternate_crypto_backend_would_need_to_verify()
    {
        // The whole point of exposing this publicly is so a runtime without a usable ECDsa (browser-wasm)
        // can verify the same signature through a different backend (e.g. Web Crypto SubtleCrypto) and get
        // an identical answer to ECDsa.VerifyData — so an independently-constructed ECDsa MUST accept the
        // recorded signature against exactly these bytes, with no other knowledge of the entry's fields.
        using var key = DumpCertificateLedgerLog.LoadPrivateKey(DumpCertificateLedgerLog.GenerateKey().PrivateKeyBase64);
        var entry = DumpCertificateLedgerLog.CreateSubmission("disc-a", "aa11", key, label: "L", utc: "2026-01-01T00:00:00Z");

        byte[] bytes = DumpCertificateLedgerLog.GetSubmitterSigningBytes(entry);

        using var verifier = System.Security.Cryptography.ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(System.Convert.FromBase64String(entry.SubmitterPublicKey), out _);
        bool ok = verifier.VerifyData(bytes, System.Convert.FromBase64String(entry.SubmitterSignature),
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        Assert.True(ok);

        // And it must be sensitive to the claim content — not some unrelated fixed value.
        var other = DumpCertificateLedgerLog.CreateSubmission("disc-b", "bb22", key, utc: "2026-01-01T00:00:00Z");
        Assert.NotEqual(bytes, DumpCertificateLedgerLog.GetSubmitterSigningBytes(other));
    }

    [Fact]
    public void Consensus_for_an_unknown_fingerprint_is_empty_and_not_disputed()
    {
        var ledger = new DumpCertificateLedger();
        Submit(ledger, "disc-a", "aa11");

        var consensus = DumpCertificateLedgerLog.Consensus(ledger, "disc-nonexistent");

        Assert.Equal(0, consensus.TotalSubmissions);
        Assert.False(consensus.Disputed);
        Assert.Null(consensus.Leading);
    }
}
