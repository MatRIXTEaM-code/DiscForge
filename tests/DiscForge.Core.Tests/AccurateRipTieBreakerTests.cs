// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Audio;
using DiscForge.Core.Recovery;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The AccurateRip tie-breaker is the missing piece for merging audio-track disagreements: a data sector
/// can prove itself via EDC, but an audio sector cannot, so today a disagreement between copies falls back
/// to an unconfirmable byte vote (<see cref="MergeMethod.VoteBestEffort"/>). These tests pin that, given a
/// known-good AccurateRip database record, the tie-breaker can instead identify which whole candidate copy
/// is actually correct — a real external check, not a vote — and that <see cref="ProvenanceMerge"/> uses it
/// ahead of the byte vote without disturbing any of its existing sector-level behaviour when no track hint
/// is supplied (or no candidate matches).
/// </summary>
public class AccurateRipTieBreakerTests
{
    private const int SS = AccurateRip.BytesPerSector;   // 2352

    /// <summary>Build one track's raw PCM: <paramref name="sectors"/> sectors of a deterministic, non-zero
    /// pattern seeded by <paramref name="seed"/>, so different seeds produce different (but each internally
    /// consistent) checksums.</summary>
    private static byte[] Track(int sectors, byte seed)
    {
        var b = new byte[sectors * SS];
        for (int i = 0; i < b.Length; i++) b[i] = (byte)((i * 7 + seed) & 0xFF);
        return b;
    }

    private static AccurateRip.DbEntry Entry(int confidence, params uint[] checksumsByTrack) => new()
    {
        Confidence = confidence,
        TrackChecksums = checksumsByTrack,
    };

    [Fact]
    public void Resolve_picks_the_one_candidate_whose_track_matches_the_database()
    {
        var good = Track(4, seed: 1);
        var bad = Track(4, seed: 2);   // a different (disagreeing) copy of the "same" track
        var known = AccurateRip.Compute(good, isFirstTrack: false, isLastTrack: false);
        var db = new[] { Entry(confidence: 3, known.V2) };   // track index 0

        var res = AccurateRipTieBreaker.Resolve(
            new[] { bad, good }, startSector: 0, endSectorInclusive: 3, sectorSize: SS,
            isFirstTrack: false, isLastTrack: false, database: db, trackIndex: 0);

        Assert.Equal(1, res.SourceIndex);   // "good" is candidate index 1
        Assert.Equal(AccurateRip.TrackStatus.MatchV2, res.Status);
        Assert.Equal(3, res.Confidence);
    }

    [Fact]
    public void Resolve_returns_no_match_when_no_candidate_agrees_with_the_database()
    {
        var a = Track(4, seed: 1);
        var b = Track(4, seed: 2);
        var db = new[] { Entry(confidence: 5, 0xDEADBEEF) };   // matches neither candidate

        var res = AccurateRipTieBreaker.Resolve(
            new[] { a, b }, 0, 3, SS, false, false, db, trackIndex: 0);

        Assert.Null(res.SourceIndex);
        Assert.Equal(AccurateRip.TrackStatus.NotFound, res.Status);
    }

    [Fact]
    public void Resolve_prefers_the_higher_confidence_match_when_more_than_one_candidate_matches()
    {
        var a = Track(4, seed: 1);
        var b = Track(4, seed: 2);
        var csA = AccurateRip.Compute(a, false, false);
        var csB = AccurateRip.Compute(b, false, false);
        // Two separate pressings/submissions, one for each candidate's checksum, B at higher confidence.
        var db = new[] { Entry(2, csA.V2), Entry(9, csB.V2) };

        var res = AccurateRipTieBreaker.Resolve(new[] { a, b }, 0, 3, SS, false, false, db, trackIndex: 0);

        Assert.Equal(1, res.SourceIndex);   // B, the higher-confidence match
        Assert.Equal(9, res.Confidence);
    }

    [Fact]
    public void Resolve_keeps_the_earliest_candidate_on_an_exact_confidence_tie()
    {
        // Two DIFFERENT candidate copies that both happen to already agree byte-for-byte and both
        // therefore match the database at the same confidence — the earliest (lowest index) wins,
        // mirroring ProvenanceMerge's own existing "earliest wins a tie" convention.
        var a = Track(4, seed: 1);
        var aCopy = Track(4, seed: 1);
        var cs = AccurateRip.Compute(a, false, false);
        var db = new[] { Entry(4, cs.V2) };

        var res = AccurateRipTieBreaker.Resolve(new[] { a, aCopy }, 0, 3, SS, false, false, db, trackIndex: 0);

        Assert.Equal(0, res.SourceIndex);
    }

    [Fact]
    public void Resolve_returns_no_match_for_an_empty_database()
    {
        var a = Track(2, 1);
        var res = AccurateRipTieBreaker.Resolve(new[] { a }, 0, 1, SS, false, false,
            Array.Empty<AccurateRip.DbEntry>(), trackIndex: 0);
        Assert.Null(res.SourceIndex);
    }

    [Fact]
    public void Resolve_skips_a_candidate_too_short_to_cover_the_track_instead_of_throwing()
    {
        var good = Track(4, 1);
        var tooShort = Track(2, 9);   // shorter than the requested 4-sector span
        var known = AccurateRip.Compute(good, false, false);
        var db = new[] { Entry(1, known.V2) };

        var res = AccurateRipTieBreaker.Resolve(new[] { tooShort, good }, 0, 3, SS, false, false, db, trackIndex: 0);

        Assert.Equal(1, res.SourceIndex);   // only the full-length candidate was even considered
    }

    [Fact]
    public void ProvenanceMerge_uses_the_tie_breaker_ahead_of_the_byte_vote_for_a_hinted_track()
    {
        var good = Track(4, seed: 1);
        var bad = Track(4, seed: 2);
        // isFirstTrack/isLastTrack false: a 4-sector track is smaller than AccurateRip's 5-sector
        // guard band, so trimming both ends here would degenerate to an empty (always-zero,
        // ambiguous) checksum range — a real disc's tracks are far longer, this is just a small
        // fixture standing in for a middle track.
        var known = AccurateRip.Compute(good, isFirstTrack: false, isLastTrack: false);
        var db = new[] { Entry(3, known.V2) };

        var hint = new ProvenanceMerge.AudioTrackHint(
            TrackIndex: 0, StartSector: 0, EndSectorInclusive: 3,
            IsFirstTrack: false, IsLastTrack: false, Database: db);

        var r = ProvenanceMerge.Merge(new[] { bad, good }, holeMaps: null, sectorSize: SS,
                                       audioHints: new[] { hint });

        Assert.Equal(4, r.Certificate.AccurateRipConfirmed);
        Assert.Equal(0, r.Certificate.VoteBestEffort);   // never reached the byte vote
        Assert.True(good.AsSpan().SequenceEqual(r.Image));   // the confirmed (good) copy's bytes were used, whole
        Assert.All(r.Certificate.Runs, run => Assert.Equal(MergeMethod.AccurateRipConfirmed, run.Method));
        Assert.Contains("AccurateRip-confirmed", r.Certificate.Summary());
    }

    [Fact]
    public void ProvenanceMerge_falls_back_to_the_byte_vote_when_the_hinted_track_matches_nothing()
    {
        var a = Track(4, seed: 1);
        var b = Track(4, seed: 2);
        var db = new[] { Entry(1, 0xDEADBEEF) };   // matches neither copy

        var hint = new ProvenanceMerge.AudioTrackHint(0, 0, 3, true, true, db);
        var r = ProvenanceMerge.Merge(new[] { a, b }, holeMaps: null, sectorSize: SS, audioHints: new[] { hint });

        Assert.Equal(0, r.Certificate.AccurateRipConfirmed);
        Assert.Equal(4, r.Certificate.VoteBestEffort);   // ordinary best-effort vote, exactly as before this feature existed
    }

    [Fact]
    public void Merging_with_no_audio_hints_at_all_is_unaffected()
    {
        var a = Track(4, seed: 1);
        var b = Track(4, seed: 1);   // identical — would AllAgree either way
        var r = ProvenanceMerge.Merge(new[] { a, b }, holeMaps: null, sectorSize: SS);

        Assert.Equal(4, r.Certificate.AllAgree);
        Assert.Equal(0, r.Certificate.AccurateRipConfirmed);
        Assert.DoesNotContain("AccurateRip-confirmed", r.Certificate.Summary());
    }

    [Fact]
    public void An_old_style_certificate_missing_the_new_field_still_deserializes_and_verifies()
    {
        var r = ProvenanceMerge.Merge(new[] { Track(3, 1), Track(3, 1) }, holeMaps: null, sectorSize: SS);
        var (privB64, _) = DiscForge.Core.Preservation.DumpLineageLog.GenerateKey();
        using var priv = DiscForge.Core.Preservation.DumpLineageLog.LoadPrivateKey(privB64);
        var signed = r.Certificate.Sign(priv);

        // Simulate a certificate written before this field existed: strip it out of the JSON entirely.
        var json = System.Text.Json.JsonSerializer.Serialize(signed, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        });
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var old = new System.Text.Json.Nodes.JsonObject();
        foreach (var prop in doc.RootElement.EnumerateObject())
            if (prop.Name != "AccurateRipConfirmed")
                old[prop.Name] = System.Text.Json.Nodes.JsonNode.Parse(prop.Value.GetRawText());

        var path = Path.Combine(Path.GetTempPath(), "dforge_dmc_old_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, old.ToJsonString());
            var back = MergeCertificate.Load(path);
            Assert.Equal(0, back.AccurateRipConfirmed);
            Assert.True(back.VerifySignature());   // the pre-existing signature is unaffected by the new field
        }
        finally { File.Delete(path); }
    }
}
