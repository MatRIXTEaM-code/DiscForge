// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Globalization;
using System.Text;

namespace DiscForge.Core.Mmc;

/// <summary>
/// A single SCSI/MMC command captured from a drive: the CDB (command descriptor
/// block) and any DATA-OUT payload the host sent with it.
/// </summary>
public sealed record MmcCommand(byte[] Cdb, byte[] DataOut, string? Direction = null)
{
    public byte Opcode => Cdb.Length > 0 ? Cdb[0] : (byte)0;
    public string OpcodeName => MmcTrace.OpcodeName(Opcode);
}

/// <summary>
/// Parses a captured MMC command trace into <see cref="MmcCommand"/>s. The trace
/// is what you get by watching a drive with a bus/SPTI sniffer while a tool
/// performs an operation (e.g. ImgBurn setting the book type on your own LG or
/// Pioneer writer). DiscForge reads that capture; it does not invent the bytes.
///
/// Format (line-based, forgiving):
/// <code>
///   # a comment
///   CDB:  BF 00 00 00 00 00 00 A1 00 04 00 00
///   DATA: 00 02 00 00 01 00 00 00
///   ---                              (blank line or --- separates commands)
///   CDB 55 10 00 ...                 ("CDB" / "CDB:" both accepted)
/// </code>
/// Hex may use spaces, commas, or 0x prefixes. A DATA / DATA-OUT / OUT line (and
/// any bare hex continuation lines that follow it) is the payload for the
/// preceding CDB.
/// </summary>
public static class MmcTrace
{
    public sealed record ParseResult(IReadOnlyList<MmcCommand> Commands, IReadOnlyList<string> Errors);

    public static ParseResult Parse(string text)
    {
        var commands = new List<MmcCommand>();
        var errors = new List<string>();

        byte[]? cdb = null;
        var data = new List<byte>();
        string? dir = null;
        bool inData = false;
        int lineNo = 0;

        void Flush()
        {
            if (cdb is not null)
                commands.Add(new MmcCommand(cdb, data.ToArray(), dir));
            cdb = null; data = new List<byte>(); dir = null; inData = false;
        }

        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            string line = raw.Trim();
            if (line.Length == 0 || line == "---") { Flush(); continue; }
            if (line.StartsWith('#') || line.StartsWith("//")) continue;

            var (label, rest) = SplitLabel(line);
            switch (label)
            {
                case "cdb":
                    Flush();
                    if (TryHex(rest, out var cb, out var err)) cdb = cb;
                    else errors.Add($"line {lineNo}: bad CDB hex ({err}).");
                    inData = false;
                    break;

                case "data":
                case "data-out":
                case "out":
                case "dataout":
                    if (cdb is null) { errors.Add($"line {lineNo}: DATA before any CDB — ignored."); break; }
                    dir = "out";
                    inData = true;
                    if (TryHex(rest, out var db, out var e2)) data.AddRange(db);
                    else if (rest.Length > 0) errors.Add($"line {lineNo}: bad DATA hex ({e2}).");
                    break;

                case null:
                    // A bare hex line: continuation of the CDB or the DATA block.
                    if (TryHex(line, out var more, out var e3))
                    {
                        if (inData) data.AddRange(more);
                        else if (cdb is not null) cdb = Concat(cdb, more);
                        else errors.Add($"line {lineNo}: hex before any CDB — ignored.");
                    }
                    else errors.Add($"line {lineNo}: unrecognised line ({e3}).");
                    break;

                default:
                    // A known-but-ignored label (e.g. "DIR:", "SENSE:") — skip its value.
                    break;
            }
        }
        Flush();
        return new ParseResult(commands, errors);
    }

    private static (string? label, string rest) SplitLabel(string line)
    {
        int colon = line.IndexOf(':');
        string head = (colon >= 0 ? line[..colon] : line.Split(' ', 2)[0]).Trim().ToLowerInvariant();
        string rest = colon >= 0 ? line[(colon + 1)..].Trim()
                    : line.Contains(' ') ? line[(line.IndexOf(' ') + 1)..].Trim() : "";
        return head switch
        {
            "cdb" => ("cdb", rest),
            "data" or "data-out" or "dataout" or "out" => (head, rest),
            "dir" or "sense" or "status" or "cmd" or "name" => (head, rest),
            _ => (null, line),                              // bare hex or unknown
        };
    }

    private static bool TryHex(string s, out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        error = "";
        if (s.Length == 0) { return true; }
        var tokens = s.Replace(",", " ").Replace("0x", " ", StringComparison.OrdinalIgnoreCase)
                      .Split(new[] { ' ', '\t', '-' }, StringSplitOptions.RemoveEmptyEntries);
        var list = new List<byte>(tokens.Length);
        foreach (var t in tokens)
        {
            // Support both "BF 00" and a run like "BF0000".
            if (t.Length == 2)
            {
                if (!byte.TryParse(t, NumberStyles.HexNumber, null, out var b)) { error = $"'{t}'"; return false; }
                list.Add(b);
            }
            else if (t.Length % 2 == 0)
            {
                for (int i = 0; i < t.Length; i += 2)
                    if (byte.TryParse(t.AsSpan(i, 2), NumberStyles.HexNumber, null, out var b)) list.Add(b);
                    else { error = $"'{t}'"; return false; }
            }
            else { error = $"'{t}' (odd length)"; return false; }
        }
        bytes = list.ToArray();
        return true;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0); b.CopyTo(r, a.Length);
        return r;
    }

    public static string Hex(ReadOnlySpan<byte> b)
    {
        var sb = new StringBuilder(b.Length * 3);
        for (int i = 0; i < b.Length; i++) { if (i > 0) sb.Append(' '); sb.Append(b[i].ToString("X2")); }
        return sb.ToString();
    }

    /// <summary>Human name for an MMC/SCSI opcode (public command set).</summary>
    public static string OpcodeName(byte op) => op switch
    {
        0x00 => "TEST UNIT READY",
        0x12 => "INQUIRY",
        0x15 => "MODE SELECT(6)",
        0x1A => "MODE SENSE(6)",
        0x1B => "START STOP UNIT",
        0x1E => "PREVENT ALLOW MEDIUM REMOVAL",
        0x25 => "READ CAPACITY",
        0x28 => "READ(10)",
        0x2A => "WRITE(10)",
        0x35 => "SYNCHRONIZE CACHE",
        0x43 => "READ TOC/PMA/ATIP",
        0x46 => "GET CONFIGURATION",
        0x4A => "GET EVENT STATUS NOTIFICATION",
        0x51 => "READ DISC INFORMATION",
        0x52 => "READ TRACK INFORMATION",
        0x53 => "RESERVE TRACK",
        0x54 => "SEND OPC INFORMATION",
        0x55 => "MODE SELECT(10)",
        0x5A => "MODE SENSE(10)",
        0x5B => "CLOSE TRACK/SESSION",
        0x5C => "READ BUFFER CAPACITY",
        0xAD => "READ DISC STRUCTURE",
        0xB6 => "SET STREAMING",
        0xBB => "SET CD SPEED",
        0xBE => "READ CD",
        0xBF => "SEND DISC STRUCTURE",         // a.k.a. SEND DVD STRUCTURE
        >= 0xE0 => $"VENDOR-SPECIFIC (0x{op:X2})",
        _ => $"opcode 0x{op:X2}",
    };
}
