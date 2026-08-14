// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Files;

namespace DiscForge.Core.PlayStation;

/// <summary>Which console a SYSTEM.CNF describes.</summary>
public enum PsConsole { Ps1, Ps2 }

/// <summary>What a PlayStation disc's SYSTEM.CNF says about it.</summary>
public sealed record PsDiscId
{
    public required PsConsole Console { get; init; }
    /// <summary>The boot path as written, e.g. <c>cdrom0:\SLUS_200.02;1</c>.</summary>
    public required string BootPath { get; init; }
    /// <summary>The normalised serial, e.g. <c>SLUS-20002</c>. Empty if the boot
    /// path is not a recognisable serial.</summary>
    public required string GameId { get; init; }
    public required string Region { get; init; }
    public string? Version { get; init; }
    public string? VideoMode { get; init; }
}

public sealed class SystemCnfException(string message) : Exception(message);

/// <summary>
/// Reads a PlayStation disc's SYSTEM.CNF — the small text file at the root of a
/// PS1 or PS2 disc that names the boot executable, and with it the game's serial,
/// region and video mode. This is identification, not circumvention: it reads a
/// plain text file from the disc's own filesystem and decrypts nothing. It is the
/// PlayStation analogue of the Dreamcast IP.BIN reader — it answers "what disc is
/// this?" for a backup a person already holds.
///
/// The file is a handful of <c>KEY = VALUE</c> lines:
///
///   BOOT2 = cdrom0:\SLUS_200.02;1      (PS2 — BOOT2 marks a PS2 disc)
///   VER   = 1.00
///   VMODE = NTSC
///
/// PS1 discs use <c>BOOT</c> (no "2"). The boot path's filename is the serial,
/// e.g. <c>SLUS_200.02</c> → <c>SLUS-20002</c>, whose third letter gives the
/// region (U = USA, E = Europe, P = Japan, K = Korea, A = Asia).
/// </summary>
public static class SystemCnf
{
    /// <summary>Parse SYSTEM.CNF text.</summary>
    public static PsDiscId Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            if (!kv.ContainsKey(key)) kv[key] = value;
        }

        PsConsole console;
        string bootPath;
        if (kv.TryGetValue("BOOT2", out var boot2))
        {
            console = PsConsole.Ps2;
            bootPath = boot2;
        }
        else if (kv.TryGetValue("BOOT", out var boot))
        {
            console = PsConsole.Ps1;
            bootPath = boot;
        }
        else
        {
            throw new SystemCnfException(
                "SYSTEM.CNF has no BOOT or BOOT2 line, so it names no boot executable — " +
                "this may not be a PlayStation disc.");
        }

        string serial = ExtractSerial(bootPath);
        return new PsDiscId
        {
            Console = console,
            BootPath = bootPath,
            GameId = NormaliseSerial(serial),
            Region = RegionOf(serial),
            Version = kv.TryGetValue("VER", out var ver) ? ver : null,
            VideoMode = kv.TryGetValue("VMODE", out var vm) ? vm : null,
        };
    }

    /// <summary>
    /// Locate and read SYSTEM.CNF from a disc image (.iso / .cdi / .bin, whichever
    /// filesystem it carries) and parse it. Returns null if the image has no
    /// SYSTEM.CNF (not a PlayStation disc, or an unreadable filesystem).
    /// </summary>
    public static PsDiscId? FromImage(string imagePath)
    {
        ArgumentNullException.ThrowIfNull(imagePath);

        var listing = ImageBrowser.List(imagePath);
        if (listing.Error is not null) return null;

        var entry = listing.Files.FirstOrDefault(f =>
            Path.GetFileName(f.Path).Equals("SYSTEM.CNF", StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;

        string temp = Path.Combine(Path.GetTempPath(), "dforge_syscnf_" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = ImageBrowser.Extract(imagePath, new[] { entry }, temp, null);
            if (result.Extracted == 0) return null;

            string extracted = Path.Combine(temp,
                entry.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(extracted)) return null;

            return Parse(File.ReadAllText(extracted, Encoding.ASCII));
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    // ---- serial + region ----------------------------------------------------

    /// <summary>Pull the serial token out of a boot path: the filename, minus the
    /// <c>;1</c> version suffix. e.g. <c>cdrom0:\SLUS_200.02;1</c> → <c>SLUS_200.02</c>.</summary>
    internal static string ExtractSerial(string bootPath)
    {
        string s = bootPath.Trim();
        int slash = s.LastIndexOfAny(new[] { '\\', '/', ':' });
        if (slash >= 0) s = s[(slash + 1)..];
        int semi = s.IndexOf(';');
        if (semi >= 0) s = s[..semi];
        return s.Trim();
    }

    /// <summary>Normalise <c>SLUS_200.02</c> to <c>SLUS-20002</c>. Non-serial boot
    /// files (some homebrew) return empty.</summary>
    internal static string NormaliseSerial(string serial)
    {
        // A serial is four letters, then digits (with an underscore and a dot as
        // separators). Anything else is not a standard serial.
        var letters = new string(serial.TakeWhile(char.IsLetter).ToArray());
        if (letters.Length != 4) return "";
        var digits = new string(serial.Skip(4).Where(char.IsDigit).ToArray());
        if (digits.Length < 4) return "";
        return $"{letters.ToUpperInvariant()}-{digits}";
    }

    internal static string RegionOf(string serial)
    {
        var letters = serial.TakeWhile(char.IsLetter).ToArray();
        if (letters.Length < 3) return "Unknown";
        return char.ToUpperInvariant(letters[2]) switch
        {
            'U' => "USA (NTSC-U)",
            'E' => "Europe (PAL)",
            'P' => "Japan (NTSC-J)",
            'K' => "Korea",
            'A' => "Asia",
            _ => "Unknown",
        };
    }
}
