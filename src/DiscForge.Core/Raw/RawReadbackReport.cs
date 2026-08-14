// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Globalization;
using System.Text;

namespace DiscForge.Core.Raw;

/// <summary>
/// Renders a <see cref="RawReadbackCompare.Report"/> as a shareable
/// burn-validation certificate (self-contained HTML) or as JSON. The HTML is
/// the artifact ImgBurn cannot produce: a sector-level proof that a burn is
/// byte-faithful across the main channel *and* the sub-channel, ready to keep
/// beside a dump or attach to a submission.
/// </summary>
public static class RawReadbackReport
{
    public static string Json(RawReadbackCompare.Report r, string golden, string readback,
                              long goldenBytes, long readbackBytes)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append("\"grade\":").Append(Q(r.Result.ToString())).Append(',');
        sb.Append("\"golden\":").Append(Q(golden)).Append(',');
        sb.Append("\"goldenBytes\":").Append(goldenBytes).Append(',');
        sb.Append("\"readback\":").Append(Q(readback)).Append(',');
        sb.Append("\"readbackBytes\":").Append(readbackBytes).Append(',');
        sb.Append("\"sectorsCompared\":").Append(r.SectorsCompared).Append(',');
        sb.Append("\"mainMismatches\":").Append(r.MainMismatches).Append(',');
        sb.Append("\"edcBroken\":").Append(r.EdcBroken).Append(',');
        sb.Append("\"scrambleNormalized\":").Append(r.ScrambleNormalized).Append(',');
        sb.Append("\"subMismatches\":").Append(r.SubMismatches).Append(',');
        sb.Append("\"misAddressed\":").Append(r.MisAddressed).Append(',');
        sb.Append("\"protectionLosses\":").Append(r.ProtectionLosses).Append(',');
        sb.Append("\"subTimingOnly\":").Append(r.SubTimingOnly).Append(',');
        sb.Append("\"dropouts\":").Append(r.Dropouts).Append(',');
        sb.Append("\"examples\":[");
        for (int i = 0; i < r.Examples.Count; i++)
        {
            var d = r.Examples[i];
            if (i > 0) sb.Append(',');
            sb.Append('{')
              .Append("\"sector\":").Append(d.AbsoluteSector).Append(',')
              .Append("\"category\":").Append(Q(d.Category)).Append(',')
              .Append("\"severity\":").Append(Q(d.Severity.ToString())).Append(',')
              .Append("\"detail\":").Append(Q(d.Detail))
              .Append('}');
        }
        sb.Append("],");
        sb.Append("\"notes\":[");
        for (int i = 0; i < r.Notes.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Q(r.Notes[i]));
        }
        sb.Append("],");
        sb.Append("\"summary\":").Append(Q(r.Summary));
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>A self-contained HTML certificate. <paramref name="utcStamp"/> is
    /// injected (not read from the clock) so the output is deterministic and testable.</summary>
    public static string Html(RawReadbackCompare.Report r, string golden, string readback,
                              long goldenBytes, long readbackBytes, string? utcStamp = null)
    {
        (string label, string color, string bg) badge = r.Result switch
        {
            RawReadbackCompare.Grade.Pass => ("PASS — byte-faithful", "#0a5", "#e9f9f0"),
            RawReadbackCompare.Grade.PassWithNotes => ("PASS (with notes)", "#b8860b", "#fbf6e7"),
            _ => ("FAIL", "#c0392b", "#fdecea"),
        };

        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>DiscForge burn-validation certificate</title><style>");
        sb.Append("body{font:15px/1.5 system-ui,-apple-system,Segoe UI,Roboto,sans-serif;color:#1c2430;max-width:820px;margin:2rem auto;padding:0 1rem}");
        sb.Append("h1{font-size:1.35rem;margin:0 0 .2rem}.sub{color:#5b6672;margin:0 0 1.2rem}");
        sb.Append(".badge{display:inline-block;padding:.5rem 1rem;border-radius:8px;font-weight:700;font-size:1.1rem}");
        sb.Append("table{border-collapse:collapse;width:100%;margin:1rem 0}");
        sb.Append("th,td{text-align:left;padding:.45rem .6rem;border-bottom:1px solid #e6e9ee;vertical-align:top}");
        sb.Append("th{color:#5b6672;font-weight:600;width:14rem}code{background:#f4f6f8;padding:.05rem .3rem;border-radius:4px}");
        sb.Append(".ok{color:#0a5}.warn{color:#b8860b}.bad{color:#c0392b;font-weight:600}");
        sb.Append(".n{font-variant-numeric:tabular-nums}.muted{color:#5b6672;font-size:.9rem}");
        sb.Append("</style></head><body>");

        sb.Append("<h1>RAW burn-validation certificate</h1>");
        sb.Append("<p class=\"sub\">A sector-level proof that the burn matches the golden image byte-for-byte across the main channel <em>and</em> the sub-channel — the check a verify-by-MD5 cannot make.</p>");
        sb.Append($"<p><span class=\"badge\" style=\"color:{badge.color};background:{badge.bg}\">{E(badge.label)}</span></p>");
        sb.Append($"<p>{E(r.Summary)}</p>");

        sb.Append("<table>");
        Row(sb, "Golden image", $"<code>{E(golden)}</code> <span class=\"muted\">({goldenBytes:N0} bytes)</span>");
        Row(sb, "Read-back capture", $"<code>{E(readback)}</code> <span class=\"muted\">({readbackBytes:N0} bytes)</span>");
        Row(sb, "Program sectors compared", $"<span class=\"n\">{r.SectorsCompared:N0}</span>");
        if (utcStamp is not null) Row(sb, "Verified (UTC)", E(utcStamp));
        sb.Append("</table>");

        sb.Append("<h2 style=\"font-size:1.05rem\">Findings</h2><table>");
        MetricRow(sb, "Main channel mismatches", r.MainMismatches, r.MainMismatches == 0);
        MetricRow(sb, "…of which broke EDC", r.EdcBroken, r.EdcBroken == 0);
        if (r.ScrambleNormalized > 0)
            MetricRow(sb, "Descrambled-on-read (content identical)", r.ScrambleNormalized, false, warnIfNonZero: true);
        MetricRow(sb, "Sub-channel differences", r.SubMismatches, r.SubMismatches == 0, warnIfNonZero: true);
        MetricRow(sb, "Mis-addressed sectors", r.MisAddressed, r.MisAddressed == 0);
        MetricRow(sb, "Protection-loss sectors", r.ProtectionLosses, r.ProtectionLosses == 0);
        MetricRow(sb, "Sub-channel timing-only", r.SubTimingOnly, r.SubTimingOnly == 0, warnIfNonZero: true);
        MetricRow(sb, "Dropouts (missing sectors)", r.Dropouts, r.Dropouts == 0);
        sb.Append("</table>");

        if (r.Examples.Count > 0)
        {
            sb.Append("<h2 style=\"font-size:1.05rem\">First differences</h2>");
            sb.Append("<table><tr><th style=\"width:6rem\">Sector</th><th style=\"width:6rem\">Severity</th><th>Category / detail</th></tr>");
            foreach (var d in r.Examples)
            {
                string cls = d.Severity == RawReadbackCompare.Severity.Defect ? "bad" : "warn";
                sb.Append($"<tr><td class=\"n\">{d.AbsoluteSector:N0}</td>")
                  .Append($"<td class=\"{cls}\">{E(d.Severity.ToString())}</td>")
                  .Append($"<td><strong>{E(d.Category)}</strong> — {E(d.Detail)}</td></tr>");
            }
            sb.Append("</table>");
        }

        if (r.Notes.Count > 0)
        {
            sb.Append("<h2 style=\"font-size:1.05rem\">Notes</h2><ul>");
            foreach (var n in r.Notes) sb.Append($"<li>{E(n)}</li>");
            sb.Append("</ul>");
        }

        sb.Append("<p class=\"muted\">Generated by DiscForge <code>raw-verify-readback</code>. ");
        sb.Append("The read-back is aligned to the golden by decoded disc address; ancillary sub-channel bytes a drive re-derives are reported as timing-only and do not fail the burn.</p>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string k, string vHtml)
        => sb.Append($"<tr><th>{E(k)}</th><td>{vHtml}</td></tr>");

    private static void MetricRow(StringBuilder sb, string k, long value, bool good, bool warnIfNonZero = false)
    {
        string cls = good ? "ok" : warnIfNonZero ? "warn" : "bad";
        string mark = good ? "✓" : warnIfNonZero ? "!" : "✗";
        sb.Append($"<tr><th>{E(k)}</th><td class=\"{cls} n\">{mark} {value:N0}</td></tr>");
    }

    private static string E(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    private static string Q(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                _ => c.ToString(),
            });
        sb.Append('"');
        return sb.ToString();
    }
}
