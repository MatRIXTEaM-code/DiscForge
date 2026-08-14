// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Dat;

/// <summary>One catalogued dump for a DAT being built: its game name, file name, size and hashes.</summary>
public sealed record DatBuildRom(string Game, string Name, long Size, string? Crc32, string? Md5, string? Sha1);

/// <summary>
/// Builds a Redump / No-Intro–style Logiqx DAT from a set of already-hashed dumps — the write side that
/// complements <see cref="DatFile"/> (which reads and verifies against a DAT) and <c>dat-verify</c>. Where
/// <see cref="DatWriter"/> re-emits a filtered slice of an existing DAT, this catalogues <i>your own</i>
/// dumps: hand it each file's size and CRC-32/MD5/SHA-1 and it writes a datfile that any DAT-driven tool —
/// including DiscForge's own verifier — can consume, turning a folder of dumps into its own reference set.
/// The output round-trips through <see cref="DatFile.ParseText"/>. Emits text; it touches no files itself.
/// </summary>
public static class DatBuilder
{
    /// <summary>Emit a Logiqx datfile for the given roms (one &lt;game&gt; per rom).</summary>
    public static string Build(string name, IEnumerable<DatBuildRom> roms,
                               string? description = null, string? author = null, string? version = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(roms);

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\"?>");
        sb.AppendLine("<datafile>");
        sb.AppendLine("  <header>");
        sb.AppendLine($"    <name>{Esc(name)}</name>");
        sb.AppendLine($"    <description>{Esc(description ?? name)}</description>");
        sb.AppendLine($"    <version>{Esc(version ?? "1.0")}</version>");
        sb.AppendLine($"    <author>{Esc(author ?? "DiscForge")}</author>");
        sb.AppendLine("  </header>");

        foreach (var r in roms)
        {
            sb.AppendLine($"  <game name=\"{Esc(r.Game)}\">");
            sb.AppendLine($"    <description>{Esc(r.Game)}</description>");
            sb.Append($"    <rom name=\"{Esc(r.Name)}\" size=\"{r.Size}\"");
            if (!string.IsNullOrEmpty(r.Crc32)) sb.Append($" crc=\"{Esc(r.Crc32!.ToLowerInvariant())}\"");
            if (!string.IsNullOrEmpty(r.Md5)) sb.Append($" md5=\"{Esc(r.Md5!.ToLowerInvariant())}\"");
            if (!string.IsNullOrEmpty(r.Sha1)) sb.Append($" sha1=\"{Esc(r.Sha1!.ToLowerInvariant())}\"");
            sb.AppendLine("/>");
            sb.AppendLine("  </game>");
        }

        sb.AppendLine("</datafile>");
        return sb.ToString();
    }

    private static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? s;
}
