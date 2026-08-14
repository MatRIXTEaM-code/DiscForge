// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Library;

/// <summary>
/// Renders a <see cref="LibraryReport"/> as a friendly, self-contained HTML page — the approachable DAT-audit
/// view the community keeps asking for as a ClrMamePro alternative: colour-coded status per file, a STAGED rename
/// preview (what a rename would change, before doing it), and a clear list of what is still missing from the set.
/// It presents the report; it changes nothing on disk.
/// </summary>
public static class LibraryReportHtml
{
    public static string Render(LibraryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        (string label, string cls) Badge(LibraryStatus s) => s switch
        {
            LibraryStatus.Verified => ("VERIFIED", "s-ok"),
            LibraryStatus.Misnamed => ("RENAME", "s-ren"),
            LibraryStatus.Duplicate => ("DUPLICATE", "s-dup"),
            LibraryStatus.Unknown => ("UNKNOWN", "s-unk"),
            _ => ("UNCHECKED", "s-unc"),
        };

        var rename = report.RenamePlan();
        var sb = new StringBuilder();
        sb.Append("""
<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>DiscForge — DAT audit</title>
<style>
:root{color-scheme:light dark}
body{font:15px/1.5 system-ui,-apple-system,Segoe UI,Roboto,sans-serif;margin:0;background:#0f1115;color:#e7e9ee}
header{padding:24px 28px;border-bottom:1px solid #262a33}
h1{margin:0 0 4px;font-size:19px} h2{font-size:14px;color:#9aa3b2;text-transform:uppercase;letter-spacing:.04em;margin:26px 28px 8px}
.sub{color:#9aa3b2;font-size:13px}
.cards{display:flex;gap:12px;flex-wrap:wrap;padding:20px 28px}
.card{background:#171a21;border:1px solid #262a33;border-radius:10px;padding:14px 18px;min-width:110px}
.card .n{font-size:26px;font-weight:650}.card .l{font-size:12px;color:#9aa3b2;text-transform:uppercase;letter-spacing:.04em}
table{width:100%;border-collapse:collapse;font-size:14px}
th,td{text-align:left;padding:9px 14px;border-bottom:1px solid #20242c;vertical-align:top}
th{color:#9aa3b2;font-weight:600;font-size:12px;text-transform:uppercase;letter-spacing:.04em}
tbody tr:hover{background:#161922}
.badge{display:inline-block;padding:2px 9px;border-radius:999px;font-size:11px;font-weight:700}
.s-ok{background:#12331f;color:#7ce6a1}.s-ren{background:#3a2c14;color:#ffca7a}.s-dup{background:#22262e;color:#9aa3b2}
.s-unk{background:#2a2140;color:#c0a8ff}.s-unc{background:#22262e;color:#9aa3b2}
.mono{font-family:ui-monospace,SFMono-Regular,Menlo,monospace;color:#9aa3b2;font-size:12px}
.arrow{color:#ffca7a}.miss{color:#ff8ba0}
.wrap{padding:0 28px 34px}
</style></head><body>
<header><h1>DiscForge — DAT audit</h1>
""");
        sb.Append($"<div class=\"sub\">{Esc(report.Root)}{(report.DatName is null ? " · no DAT" : " · DAT: " + Esc(report.DatName))} · " +
                  $"{report.Total:N0} file(s)</div></header>\n");

        sb.Append("<div class=\"cards\">");
        void Card(int n, string l) => sb.Append($"<div class=\"card\"><div class=\"n\">{n:N0}</div><div class=\"l\">{l}</div></div>");
        Card(report.Verified, "verified");
        Card(report.Misnamed, "to rename");
        Card(report.Duplicates, "duplicate");
        Card(report.Unknown, "unknown");
        Card(report.Missing.Count, "missing");
        sb.Append("</div>\n");

        // Staged rename preview.
        if (rename.Count > 0)
        {
            sb.Append($"<h2>Staged renames ({rename.Count:N0}) — preview, not yet applied</h2><div class=\"wrap\"><table><tbody>\n");
            foreach (var r in rename)
                sb.Append($"<tr><td class=\"mono\">{Esc(Path.GetFileName(r.From))}</td><td class=\"arrow\">→</td><td class=\"mono\">{Esc(Path.GetFileName(r.To))}</td></tr>\n");
            sb.Append("</tbody></table></div>\n");
        }

        // Per-file table.
        sb.Append("<h2>Files</h2><div class=\"wrap\"><table><thead><tr><th>Status</th><th>File</th><th>Identity</th><th>Suggested name</th></tr></thead><tbody>\n");
        foreach (var e in report.Entries)
        {
            var (label, cls) = Badge(e.Status);
            string identity = e.Match is not null ? Esc(e.Match.Game)
                : e.RomPlatform.Length > 0 ? Esc(e.RomPlatform)
                : e.Format.Length > 0 ? Esc(e.Format) : "—";
            sb.Append("<tr>");
            sb.Append($"<td><span class=\"badge {cls}\">{label}</span></td>");
            sb.Append($"<td class=\"mono\">{Esc(e.FileName)}</td>");
            sb.Append($"<td>{identity}</td>");
            sb.Append($"<td class=\"mono\">{(e.SuggestedName is null ? "—" : Esc(e.SuggestedName))}</td>");
            sb.Append("</tr>\n");
        }
        sb.Append("</tbody></table></div>\n");

        // Missing.
        if (report.Missing.Count > 0)
        {
            sb.Append($"<h2>Missing from the set ({report.Missing.Count:N0})</h2><div class=\"wrap\"><table><tbody>\n");
            foreach (var m in report.Missing)
                sb.Append($"<tr><td class=\"miss\">{Esc(m.Name)}</td><td>{Esc(m.Game)}</td></tr>\n");
            sb.Append("</tbody></table></div>\n");
        }

        sb.Append("</body></html>\n");
        return sb.ToString();
    }
}
