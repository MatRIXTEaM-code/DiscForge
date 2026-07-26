// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;

namespace DiscForge.Core.Dat;

/// <summary>
/// Emits a minimal Logiqx-XML DAT from a set of games — used to write the filtered
/// DAT that 1G1R produces, so the trimmed set can be fed straight back into DiscForge
/// or any other DAT-driven tool. The output round-trips through <see cref="DatFile"/>.
/// </summary>
public static class DatWriter
{
    public static string WriteLogiqx(string name, IEnumerable<DatGameRef> games)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(games);

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\"?>");
        sb.AppendLine("<datafile>");
        sb.AppendLine($"  <header><name>{Esc(name)}</name><description>{Esc(name)}</description></header>");
        foreach (var g in games)
        {
            sb.AppendLine($"  <game name=\"{Esc(g.Game)}\">");
            if (g.CloneOf is not null) { /* clone links are dropped in a flat filtered set */ }
            foreach (var r in g.Roms)
            {
                sb.Append($"    <rom name=\"{Esc(r.Name)}\" size=\"{r.Size}\"");
                if (r.Crc is not null) sb.Append($" crc=\"{Esc(r.Crc)}\"");
                if (r.Md5 is not null) sb.Append($" md5=\"{Esc(r.Md5)}\"");
                if (r.Sha1 is not null) sb.Append($" sha1=\"{Esc(r.Sha1)}\"");
                sb.AppendLine("/>");
            }
            sb.AppendLine("  </game>");
        }
        sb.AppendLine("</datafile>");
        return sb.ToString();
    }

    private static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? s;
}
