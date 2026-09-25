// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiscForge.Core.Audio;
using DiscForge.Core.Preservation;

namespace DiscForge.Core.Recovery;

/// <summary>How one sector of a merged image was decided.</summary>
public enum MergeMethod
{
    /// <summary>Every surviving copy agreed byte-for-byte.</summary>
    AllAgree,
    /// <summary>One copy's sector passed its EDC — provably correct.</summary>
    EdcRecovered,
    /// <summary>A per-byte majority vote reassembled a sector that then passed its EDC.</summary>
    VoteVerified,
    /// <summary>A sector with no EDC (audio / Form 2): the majority vote, unconfirmable.</summary>
    VoteBestEffort,
    /// <summary>Only one copy had this sector at all (the others held a hole) — used as-is.</summary>
    SingleSource,
    /// <summary>No copy could supply a valid sector — a genuine hole in the reconstruction.</summary>
    Unrecovered,
    /// <summary>An audio sector with no EDC of its own: one candidate's whole track matched a known-good
    /// AccurateRip checksum, so that candidate was used for the whole track instead of an unconfirmable
    /// byte vote. See <see cref="AccurateRipTieBreaker"/>.</summary>
    AccurateRipConfirmed,
}

/// <summary>A coalesced run of sectors decided the same way, by the same source.</summary>
public sealed record ProvenanceRun(long StartSector, long EndSector, MergeMethod Method, int? Source)
{
    public long Count => EndSector - StartSector + 1;
}

/// <summary>
/// A signed, checkable account of how a merged image was reconstructed from several imperfect copies: the hash
/// of every input, the hash of the output, how each sector was decided and which copy it came from, and the
/// sectors that could not be recovered. No optical tool emits a reconstruction you can audit — a merged image is
/// normally a black box. This certificate makes the merge reproducible and its provenance verifiable: anyone
/// with the same inputs can re-run the merge, reproduce the OutputSha256, and confirm the signature. It records
/// how the image was rebuilt; it recovers nothing a copy did not hold and defeats nothing.
/// </summary>
public sealed record MergeCertificate
{
    public string FormatVersion => "dmc/1";
    public required int SourceCount { get; init; }
    public required int SectorCount { get; init; }
    public required int SectorSize { get; init; }
    /// <summary>SHA-256 of each input image, in order — binds the certificate to exact copies.</summary>
    public required IReadOnlyList<string> SourceSha256 { get; init; }
    /// <summary>SHA-256 of the reconstructed image.</summary>
    public required string OutputSha256 { get; init; }

    public required int AllAgree { get; init; }
    public required int EdcRecovered { get; init; }
    public required int VoteVerified { get; init; }
    public required int VoteBestEffort { get; init; }
    public required int SingleSource { get; init; }
    public required int Unrecovered { get; init; }
    /// <summary>Sectors excluded from a source because its bad-sector map marked them unreadable (holes not voted on).</summary>
    public required int HoleExcluded { get; init; }
    /// <summary>Sectors decided by the AccurateRip tie-breaker rather than a byte vote (see
    /// <see cref="MergeMethod.AccurateRipConfirmed"/>). Optional and defaults to 0 so certificates written
    /// before this field existed still deserialize, and — deliberately — is not part of
    /// <see cref="SigningContent"/>, so an old certificate's signature still verifies unchanged.</summary>
    public int AccurateRipConfirmed { get; init; }

    public required IReadOnlyList<ProvenanceRun> Runs { get; init; }
    public required IReadOnlyList<long> UnrecoveredSectors { get; init; }

    public string? Signature { get; init; }
    public string? PublicKey { get; init; }

    [JsonIgnore] public bool FullyRecovered => Unrecovered == 0;

    public string Summary()
    {
        string sig = Signature is null ? "unsigned" : "signed";
        return $"{SectorCount:N0} sector(s) from {SourceCount} cop{(SourceCount == 1 ? "y" : "ies")} " +
               $"({sig}): {AllAgree:N0} agreed, {EdcRecovered:N0} EDC, {VoteVerified:N0} voted, " +
               $"{VoteBestEffort:N0} best-effort, {SingleSource:N0} single-source, {Unrecovered:N0} unrecovered" +
               $"{(AccurateRipConfirmed > 0 ? $", {AccurateRipConfirmed:N0} AccurateRip-confirmed" : "")}" +
               $"{(HoleExcluded > 0 ? $"; {HoleExcluded:N0} hole(s) excluded from voting" : "")}.";
    }

    /// <summary>The canonical bytes a signature covers: the format, geometry, every input hash, the output hash
    /// and the sector tallies. Signing the output hash binds the exact reconstructed image.</summary>
    internal string SigningContent() =>
        string.Join("\n", new[]
        {
            FormatVersion, SourceCount.ToString(), SectorCount.ToString(), SectorSize.ToString(),
            string.Join(",", SourceSha256), OutputSha256,
            $"{AllAgree},{EdcRecovered},{VoteVerified},{VoteBestEffort},{SingleSource},{Unrecovered},{HoleExcluded}",
        });

    /// <summary>The exact UTF-8 bytes <see cref="VerifySignature"/> feeds to ECDSA — exposed publicly so a
    /// caller whose runtime lacks a usable <see cref="ECDsa"/> (browser-wasm has none in .NET 8; see
    /// DiscForge.Wasm) can verify the same signature through a different backend, e.g. the browser's own Web
    /// Crypto <c>SubtleCrypto</c>, and get an identical answer. <see cref="VerifySignature"/> is defined in
    /// terms of this method, not the reverse, so the two can never silently diverge.</summary>
    public byte[] GetSigningBytes() => Encoding.UTF8.GetBytes(SigningContent());

    public MergeCertificate Sign(ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        byte[] sig = privateKey.SignData(GetSigningBytes(), HashAlgorithmName.SHA256);
        return this with
        {
            Signature = System.Convert.ToBase64String(sig),
            PublicKey = System.Convert.ToBase64String(privateKey.ExportSubjectPublicKeyInfo()),
        };
    }

    /// <summary>Verify the embedded signature against the embedded public key. Deliberately does NOT catch
    /// <see cref="PlatformNotSupportedException"/> (thrown by <see cref="ECDsa.Create()"/> on browser-wasm,
    /// which has no ECDSA implementation in .NET 8) — a caller on such a runtime must see that exception and
    /// fall back to an alternate backend (e.g. the browser's own Web Crypto <c>SubtleCrypto</c>, driven by
    /// <see cref="GetSigningBytes"/>; see DiscForge.Wasm), not silently be told the signature is invalid.</summary>
    public bool VerifySignature()
    {
        if (string.IsNullOrEmpty(Signature) || string.IsNullOrEmpty(PublicKey)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(System.Convert.FromBase64String(PublicKey), out _);
            return key.VerifyData(GetSigningBytes(),
                                  System.Convert.FromBase64String(Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return false; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    public static MergeCertificate Load(string path) =>
        JsonSerializer.Deserialize<MergeCertificate>(File.ReadAllText(path), JsonOpts)
        ?? throw new InvalidDataException($"'{path}' is not a merge certificate.");
}

/// <summary>The merged image plus its certificate.</summary>
public sealed record ProvenanceMergeResult(byte[] Image, MergeCertificate Certificate);

/// <summary>
/// A multi-copy merge that (a) knows which sectors each copy could NOT read — from its bad-sector map — and
/// excludes those holes from the vote instead of letting a zero-filled sector count as evidence, and (b) records
/// where every output sector came from. The result is an image plus a signed <see cref="MergeCertificate"/>.
/// </summary>
public static class ProvenanceMerge
{
    /// <summary>One audio track's span within the merge, for the AccurateRip tie-breaker: before the
    /// normal per-sector vote runs, each hint's whole track is checked across every candidate copy (see
    /// <see cref="AccurateRipTieBreaker"/>), and if exactly one clearly matches the database it is used
    /// for that entire span, tagged <see cref="MergeMethod.AccurateRipConfirmed"/> — sectors within a
    /// hint's span are never handed to the byte vote. A track with no match falls through to the
    /// existing per-sector logic, unchanged.</summary>
    public sealed record AudioTrackHint(
        int TrackIndex, int StartSector, int EndSectorInclusive,
        bool IsFirstTrack, bool IsLastTrack, IReadOnlyList<AccurateRip.DbEntry> Database);

    public static ProvenanceMergeResult Merge(IReadOnlyList<byte[]> images, IReadOnlyList<BadSectorMap?>? holeMaps = null,
                                              int sectorSize = DumpMerge.RawSectorSize,
                                              IReadOnlyList<AudioTrackHint>? audioHints = null)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0) throw new ArgumentException("Provide at least one image to merge.", nameof(images));
        if (holeMaps is not null && holeMaps.Count != images.Count)
            throw new ArgumentException("A hole map must be supplied for every image (use null for a copy with no map).", nameof(holeMaps));

        int len = images[0].Length;
        for (int i = 1; i < images.Count; i++)
            if (images[i].Length != len)
                throw new ArgumentException($"All copies must be the same length; copy 1 is {len:N0} bytes, copy {i + 1} is {images[i].Length:N0}.");
        if (len % sectorSize != 0)
            throw new ArgumentException($"Image length {len:N0} is not a whole number of {sectorSize}-byte sectors.");

        int sectors = len / sectorSize;
        var holes = new HashSet<long>[images.Count];
        for (int k = 0; k < images.Count; k++)
            holes[k] = holeMaps?[k] is { } m ? new HashSet<long>(m.UnreadableLba) : new HashSet<long>();

        var outp = new byte[len];
        var perSector = new (MergeMethod method, int? source)[sectors];
        var decided = new bool[sectors];
        int allAgree = 0, edc = 0, voteV = 0, voteB = 0, single = 0, unrec = 0, holeExcluded = 0, arConfirmed = 0;
        var unrecList = new List<long>();

        if (audioHints is not null)
        {
            foreach (var hint in audioHints)
            {
                if (hint.StartSector < 0 || hint.EndSectorInclusive >= sectors || hint.EndSectorInclusive < hint.StartSector)
                    throw new ArgumentException(
                        $"Audio hint for track {hint.TrackIndex} spans sectors [{hint.StartSector},{hint.EndSectorInclusive}], " +
                        $"outside this merge's {sectors:N0} sectors.", nameof(audioHints));

                var resolution = AccurateRipTieBreaker.Resolve(
                    images, hint.StartSector, hint.EndSectorInclusive, sectorSize,
                    hint.IsFirstTrack, hint.IsLastTrack, hint.Database, hint.TrackIndex);
                if (resolution.SourceIndex is not int k) continue;   // no match — the byte vote decides this track instead

                int hAt = hint.StartSector * sectorSize;
                int hLen = (hint.EndSectorInclusive - hint.StartSector + 1) * sectorSize;
                images[k].AsSpan(hAt, hLen).CopyTo(outp.AsSpan(hAt, hLen));
                for (int s = hint.StartSector; s <= hint.EndSectorInclusive; s++)
                {
                    perSector[s] = (MergeMethod.AccurateRipConfirmed, k);
                    decided[s] = true;
                }
                arConfirmed += hint.EndSectorInclusive - hint.StartSector + 1;
            }
        }

        var candIdx = new List<int>(images.Count);
        for (int s = 0; s < sectors; s++)
        {
            if (decided[s]) continue;   // already settled by the AccurateRip tie-breaker above
            int at = s * sectorSize;
            candIdx.Clear();
            for (int k = 0; k < images.Count; k++)
            {
                if (holes[k].Contains(s)) { holeExcluded++; continue; }
                candIdx.Add(k);
            }

            MergeMethod method; int? src;
            if (candIdx.Count == 0)
            {
                outp.AsSpan(at, sectorSize).Clear();      // no copy had it — a genuine hole, zero-filled and recorded
                method = MergeMethod.Unrecovered; src = null; unrec++;
                if (unrecList.Count < 4096) unrecList.Add(s);
            }
            else if (candIdx.Count == 1)
            {
                int k = candIdx[0];
                images[k].AsSpan(at, sectorSize).CopyTo(outp.AsSpan(at, sectorSize));
                method = MergeMethod.SingleSource; src = k; single++;
            }
            else
            {
                method = DecideSector(images, candIdx, at, sectorSize, outp, out src);
                switch (method)
                {
                    case MergeMethod.AllAgree: allAgree++; break;
                    case MergeMethod.EdcRecovered: edc++; break;
                    case MergeMethod.VoteVerified: voteV++; break;
                    case MergeMethod.VoteBestEffort: voteB++; break;
                    case MergeMethod.Unrecovered: unrec++; if (unrecList.Count < 4096) unrecList.Add(s); break;
                }
            }
            perSector[s] = (method, src);
        }

        var runs = Coalesce(perSector);
        var sourceHashes = images.Select(Sha256).ToList();
        var cert = new MergeCertificate
        {
            SourceCount = images.Count, SectorCount = sectors, SectorSize = sectorSize,
            SourceSha256 = sourceHashes, OutputSha256 = Sha256(outp),
            AllAgree = allAgree, EdcRecovered = edc, VoteVerified = voteV, VoteBestEffort = voteB,
            SingleSource = single, Unrecovered = unrec, HoleExcluded = holeExcluded,
            AccurateRipConfirmed = arConfirmed,
            Runs = runs, UnrecoveredSectors = unrecList,
        };
        return new ProvenanceMergeResult(outp, cert);
    }

    private static MergeMethod DecideSector(IReadOnlyList<byte[]> images, List<int> cand, int at, int size,
                                            byte[] outp, out int? source)
    {
        // All surviving candidates identical?
        bool allSame = true;
        for (int i = 1; i < cand.Count && allSame; i++)
            allSame = images[cand[0]].AsSpan(at, size).SequenceEqual(images[cand[i]].AsSpan(at, size));
        if (allSame)
        {
            images[cand[0]].AsSpan(at, size).CopyTo(outp.AsSpan(at, size));
            source = cand[0];
            return MergeMethod.AllAgree;
        }

        // A single candidate that validates on its own.
        foreach (int k in cand)
            if (DumpMerge.Validate(images[k].AsSpan(at, size)) == true)
            {
                images[k].AsSpan(at, size).CopyTo(outp.AsSpan(at, size));
                source = k;
                return MergeMethod.EdcRecovered;
            }

        // Per-byte majority vote over the surviving candidates.
        var dest = outp.AsSpan(at, size);
        MajorityVote(images, cand, at, size, dest);
        source = null;
        bool? v = DumpMerge.Validate(dest);
        if (v == true) return MergeMethod.VoteVerified;
        if (v is null) return MergeMethod.VoteBestEffort;
        return MergeMethod.Unrecovered;
    }

    private static void MajorityVote(IReadOnlyList<byte[]> images, List<int> cand, int at, int size, Span<byte> dest)
    {
        int n = cand.Count;
        Span<byte> vals = n <= 64 ? stackalloc byte[n] : new byte[n];
        for (int i = 0; i < size; i++)
        {
            for (int k = 0; k < n; k++) vals[k] = images[cand[k]][at + i];
            byte best = vals[0]; int bestCount = 0;
            for (int a = 0; a < n; a++)
            {
                int c = 0;
                for (int b = 0; b < n; b++) if (vals[b] == vals[a]) c++;
                if (c > bestCount) { bestCount = c; best = vals[a]; }   // '>' keeps the earliest candidate on ties
            }
            dest[i] = best;
        }
    }

    private static IReadOnlyList<ProvenanceRun> Coalesce((MergeMethod method, int? source)[] per)
    {
        var runs = new List<ProvenanceRun>();
        if (per.Length == 0) return runs;
        long start = 0;
        for (long s = 1; s <= per.Length; s++)
        {
            if (s < per.Length && per[s] == per[(int)start]) continue;
            runs.Add(new ProvenanceRun(start, s - 1, per[(int)start].method, per[(int)start].source));
            start = s;
        }
        return runs;
    }

    private static string Sha256(byte[] data) => System.Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
