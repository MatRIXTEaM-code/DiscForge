// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DiscForge.Core.Convert;

/// <summary>
/// A shareable, self-contained certificate that a format conversion was <b>provably lossless</b>:
/// both images decode to the SAME raw disc bytes, so a single SHA-256 of that canonical content
/// attests to the pair. This is the conversion-side analogue of the burn-validation certificate —
/// it turns "we converted bin/cue → CHD" into "here is a signed statement that the CHD decodes to
/// the exact bytes the bin/cue did, hash included, verify it yourself." For a round-trip
/// (original → converted → decoded-back) it is a concrete <c>dec(enc(x)) ≡ x</c> proof for that x.
///
/// Read-only: it compares and hashes, it converts nothing. Built on <see cref="ConversionVerify"/>.
/// </summary>
public static class ConversionCertificate
{
    public sealed record Cert
    {
        public required bool Lossless { get; init; }
        public required long LengthA { get; init; }
        public required long LengthB { get; init; }
        public required int SectorSize { get; init; }
        public long Sectors => SectorSize > 0 ? LengthA / SectorSize : 0;
        public long? FirstDiffOffset { get; init; }
        public required string Sha256A { get; init; }
        public required string Sha256B { get; init; }
        /// <summary>The shared content hash when lossless (identical to both A and B); null otherwise.</summary>
        public string? ContentSha256 => Lossless ? Sha256A : null;
        public required string SourceName { get; init; }
        public required string TargetName { get; init; }
        public string? Stamp { get; init; }

        public string Summary => Lossless
            ? $"LOSSLESS — {SourceName} and {TargetName} both decode to {LengthA:N0} bytes " +
              $"({Sectors:N0} sectors); SHA-256 {ContentSha256}."
            : LengthA != LengthB
                ? $"NOT LOSSLESS — sizes differ (A={LengthA:N0}, B={LengthB:N0} bytes): a track, padding " +
                  "or sub-channel was added or dropped."
                : $"NOT LOSSLESS — same size but bytes differ, first at offset {FirstDiffOffset ?? 0:N0}.";
    }

    /// <summary>Build a certificate by decoding-compare + hashing both raw images.</summary>
    public static Cert Build(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, string sourceName, string targetName,
                             int sectorSize = ConversionVerify.CdSector, string? stamp = null)
    {
        var r = ConversionVerify.Compare(a, b, sectorSize);
        string shaA = System.Convert.ToHexString(SHA256.HashData(a)).ToLowerInvariant();
        string shaB = System.Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        return new Cert
        {
            Lossless = r.Lossless,
            LengthA = r.LengthA,
            LengthB = r.LengthB,
            SectorSize = sectorSize,
            FirstDiffOffset = r.FirstDiffOffset,
            Sha256A = shaA,
            Sha256B = shaB,
            SourceName = sourceName,
            TargetName = targetName,
            Stamp = stamp,
        };
    }

    public static string Json(Cert c)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append("\"verdict\":").Append(c.Lossless ? "\"LOSSLESS\"" : "\"NOT_LOSSLESS\"").Append(',');
        sb.Append("\"source\":").Append(Q(c.SourceName)).Append(',');
        sb.Append("\"target\":").Append(Q(c.TargetName)).Append(',');
        sb.Append("\"lengthA\":").Append(c.LengthA).Append(',');
        sb.Append("\"lengthB\":").Append(c.LengthB).Append(',');
        sb.Append("\"sectorSize\":").Append(c.SectorSize).Append(',');
        sb.Append("\"sectors\":").Append(c.Sectors).Append(',');
        sb.Append("\"sha256A\":").Append(Q(c.Sha256A)).Append(',');
        sb.Append("\"sha256B\":").Append(Q(c.Sha256B)).Append(',');
        sb.Append("\"contentSha256\":").Append(c.ContentSha256 is null ? "null" : Q(c.ContentSha256)).Append(',');
        if (c.FirstDiffOffset is { } fd) sb.Append("\"firstDiffOffset\":").Append(fd).Append(',');
        if (c.Stamp is not null) sb.Append("\"verifiedUtc\":").Append(Q(c.Stamp)).Append(',');
        sb.Append("\"summary\":").Append(Q(c.Summary));
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>A self-contained HTML certificate. <paramref name="stamp"/> is injected (not read from
    /// the clock) so the output is deterministic and testable.</summary>
    public static string Html(Cert c)
    {
        (string label, string color, string bg) badge = c.Lossless
            ? ("LOSSLESS — byte-exact", "#0a5", "#e9f9f0")
            : ("NOT LOSSLESS", "#c0392b", "#fdecea");

        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>DiscForge lossless-conversion certificate</title><style>");
        sb.Append("body{font:15px/1.5 system-ui,-apple-system,Segoe UI,Roboto,sans-serif;color:#1c2430;max-width:820px;margin:2rem auto;padding:0 1rem}");
        sb.Append("h1{font-size:1.35rem;margin:0 0 .2rem}.sub{color:#5b6672;margin:0 0 1.2rem}");
        sb.Append(".badge{display:inline-block;padding:.5rem 1rem;border-radius:8px;font-weight:700;font-size:1.1rem}");
        sb.Append("table{border-collapse:collapse;width:100%;margin:1rem 0}");
        sb.Append("th,td{text-align:left;padding:.45rem .6rem;border-bottom:1px solid #e6e9ee;vertical-align:top}");
        sb.Append("th{color:#5b6672;font-weight:600;width:14rem}code{background:#f4f6f8;padding:.05rem .3rem;border-radius:4px;word-break:break-all}");
        sb.Append(".n{font-variant-numeric:tabular-nums}.muted{color:#5b6672;font-size:.9rem}");
        sb.Append("</style></head><body>");

        sb.Append("<h1>Lossless-conversion certificate</h1>");
        sb.Append("<p class=\"sub\">A statement that two disc images decode to the same raw bytes — the check that a format conversion preserved every byte, with the content hash to verify it independently.</p>");
        sb.Append($"<p><span class=\"badge\" style=\"color:{badge.color};background:{badge.bg}\">{E(badge.label)}</span></p>");
        sb.Append($"<p>{E(c.Summary)}</p>");

        sb.Append("<table>");
        Row(sb, "Source", $"<code>{E(c.SourceName)}</code> <span class=\"muted\">({c.LengthA:N0} bytes)</span>");
        Row(sb, "Target", $"<code>{E(c.TargetName)}</code> <span class=\"muted\">({c.LengthB:N0} bytes)</span>");
        Row(sb, "Sectors compared", $"<span class=\"n\">{c.Sectors:N0}</span> × {c.SectorSize} B");
        if (c.Lossless)
            Row(sb, "Content SHA-256", $"<code>{E(c.ContentSha256!)}</code>");
        else
        {
            Row(sb, "Source SHA-256", $"<code>{E(c.Sha256A)}</code>");
            Row(sb, "Target SHA-256", $"<code>{E(c.Sha256B)}</code>");
            if (c.FirstDiffOffset is { } fd) Row(sb, "First difference", $"<span class=\"n\">offset {fd:N0}</span>");
        }
        if (c.Stamp is not null) Row(sb, "Verified (UTC)", E(c.Stamp));
        sb.Append("</table>");

        sb.Append("<p class=\"muted\">Generated by DiscForge <code>verify-convert</code>. Both images are decoded to their raw sector bytes and compared exactly; when lossless, the single content hash attests to both. This is a check a size-only or filename comparison cannot make.</p>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string k, string vHtml)
        => sb.Append($"<tr><th>{E(k)}</th><td>{vHtml}</td></tr>");

    private static string E(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string Q(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char ch in s)
            sb.Append(ch switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => "\\u" + ((int)ch).ToString("x4", CultureInfo.InvariantCulture),
                _ => ch.ToString(),
            });
        sb.Append('"');
        return sb.ToString();
    }
}
