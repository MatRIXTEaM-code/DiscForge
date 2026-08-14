// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Convert;

/// <summary>Which optical-drive emulator's SD-card layout to produce.</summary>
public enum OdeTarget
{
    /// <summary>Sega Dreamcast GDEMU — numbered folders, 01 reserved for the menu, games from 02.</summary>
    Gdemu,
    /// <summary>Sega Saturn Rhea (SD).</summary>
    Rhea,
    /// <summary>Sega Saturn Phoebe (CF).</summary>
    Phoebe,
    /// <summary>Terraonion MODE — free-form named folders, firmware scans them.</summary>
    Mode,
}

public sealed record OdeGameLayout
{
    public required string Title { get; init; }
    /// <summary>The destination folder name (e.g. "02" for a numbered ODE, or the title for MODE).</summary>
    public required string Folder { get; init; }
    public required int FilesCopied { get; init; }
}

public sealed record OdeLayoutResult
{
    public required OdeTarget Target { get; init; }
    public required string OutDir { get; init; }
    public required IReadOnlyList<OdeGameLayout> Games { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    public string Summary() => $"{Games.Count} game(s) laid out for {Target} in {OutDir}.";
}

/// <summary>
/// ode-layout — arrange a set of already-converted, verified disc images into the exact SD-card structure a
/// given optical-drive emulator expects. Every ODE wants a different layout and the community shuffles files by
/// hand for each; this does it deterministically to each device's documented convention. It only copies and
/// organises verified images and writes the per-game sidecar metadata; it never generates the device's boot menu
/// (GDMENU / RMENU build that themselves), and it defeats/decrypts nothing.
///
/// Conventions implemented (see docs): GDEMU and Rhea/Phoebe use sequentially numbered folders with folder 01
/// RESERVED for the menu — so games start at 02; MODE uses free-form named folders that its firmware scans. The
/// menu/index files (LIST.INI, the RMENU boot image, cover art) are produced by each device's own menu utility,
/// by design; this lays out the folders + images + name/disc sidecars those utilities then read.
/// </summary>
public static class OdeLayout
{
    /// <summary>
    /// Lay out each immediate sub-folder of <paramref name="gamesDir"/> (one game per sub-folder, its name the
    /// title) into <paramref name="outDir"/> for <paramref name="target"/>.
    /// </summary>
    public static OdeLayoutResult Build(OdeTarget target, string gamesDir, string outDir)
    {
        ArgumentNullException.ThrowIfNull(gamesDir);
        ArgumentNullException.ThrowIfNull(outDir);
        if (!Directory.Exists(gamesDir)) throw new DirectoryNotFoundException($"'{gamesDir}' is not a folder.");

        var games = Directory.EnumerateDirectories(gamesDir)
            .Where(d => Directory.EnumerateFiles(d).Any())
            .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (games.Count == 0)
            throw new InvalidOperationException($"'{gamesDir}' has no game sub-folders (expected one folder per game).");

        Directory.CreateDirectory(outDir);
        var laid = new List<OdeGameLayout>();
        var notes = new List<string>();

        bool numbered = target is OdeTarget.Gdemu or OdeTarget.Rhea or OdeTarget.Phoebe;
        // Numbered ODEs reserve folder 01 for the menu; games begin at 02. Width grows with the count.
        int highest = games.Count + 1;                       // last game's folder number
        int width = Math.Max(2, highest.ToString().Length);

        for (int i = 0; i < games.Count; i++)
        {
            string src = games[i];
            string title = Path.GetFileName(src);
            string folderName = numbered ? (i + 2).ToString().PadLeft(width, '0') : SanitizeName(title);
            string dest = Path.Combine(outDir, folderName);
            Directory.CreateDirectory(dest);

            int copied = 0;
            foreach (var f in Directory.EnumerateFiles(src))
            {
                File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
                copied++;
            }

            // Per-game sidecars the menu managers read (GDMENU: name.txt; Orbital/RMENU: Name.txt + Disc.txt).
            switch (target)
            {
                case OdeTarget.Gdemu:
                    File.WriteAllText(Path.Combine(dest, "name.txt"), title, Encoding.ASCII);
                    break;
                case OdeTarget.Rhea:
                case OdeTarget.Phoebe:
                    File.WriteAllText(Path.Combine(dest, "Name.txt"), title, Encoding.ASCII);
                    File.WriteAllText(Path.Combine(dest, "Disc.txt"), "1", Encoding.ASCII);
                    break;
            }

            laid.Add(new OdeGameLayout { Title = title, Folder = folderName, FilesCopied = copied });
        }

        // Device-level files and honest guidance.
        switch (target)
        {
            case OdeTarget.Gdemu:
                notes.Add("Folder 01 is RESERVED for the menu — run GDMENU Card Manager or openMenu to build it (it also writes the LIST.INI / DISCLIST.TXT index).");
                notes.Add("Images must be GDI (+ track files) or CDI — GDEMU reads neither CHD nor Redump images.");
                break;
            case OdeTarget.Rhea:
            case OdeTarget.Phoebe:
                string ini = target == OdeTarget.Rhea ? "Rhea.ini" : "Phoebe.ini";
                File.WriteAllText(Path.Combine(outDir, ini), "[settings]\nregion=auto\n", Encoding.ASCII);
                notes.Add($"Wrote a minimal {ini} at the card root — adjust region as needed.");
                notes.Add("Folder 01 is RESERVED for RMENU — build the menu (LIST.INI + RMENU boot image) with RMENU.exe or Orbital Organizer.");
                notes.Add("Preferred image format is CloneCD (CCD/IMG/SUB); ISO only for games without CD-DA audio.");
                break;
            case OdeTarget.Mode:
                notes.Add("MODE scans the folder tree and builds its own menu — no index file is written. Place these folders under the console's top-level folder on the card.");
                notes.Add("MODE reads CDI/GDI/CCD/MDF/BIN-ISO+CUE; not CHD. SATA storage must be exFAT.");
                break;
        }
        notes.Add("Menu/index files and cover art are produced by each device's own menu utility by design; this tool lays out the folders, images and sidecars they consume.");

        return new OdeLayoutResult { Target = target, OutDir = outDir, Games = laid, Notes = notes };
    }

    /// <summary>Trim a title to a filesystem-safe folder name (invalid chars removed).</summary>
    private static string SanitizeName(string name)
    {
        // Use the strict cross-platform reserved set (not the host OS's), so a layout
        // authored on Linux/macOS is still valid on the FAT/exFAT SD card it targets.
        // (Path.GetInvalidFileNameChars() on Linux flags only '/' and NUL, letting
        // ':' '*' '?' through into a folder name the target device rejects.)
        const string reserved = "<>:\"/\\|?*";
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(c < ' ' || reserved.IndexOf(c) >= 0 ? '_' : c);
        var s = sb.ToString().Trim();
        return s.Length == 0 ? "game" : s;
    }
}
