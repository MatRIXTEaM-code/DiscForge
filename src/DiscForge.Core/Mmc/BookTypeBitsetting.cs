// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Mmc;

/// <summary>
/// Clean-room bitsetting support. "Bitsetting" (book-type change) is the last
/// feature ImgBurn has that DiscForge doesn't, because the command that sets a
/// recordable disc's book type is vendor-specific magic (LG, Pioneer, Lite-On,
/// NEC and Samsung each differ) and MUST NOT be guessed — DiscForge declines to
/// ship fabricated command bytes.
///
/// Instead this is the honest, provable path: capture the command your *own*
/// drive issues (a bus/SPTI trace of a tool setting the book type), let
/// DiscForge decode what is publicly knowable about it (opcode, obvious fields,
/// the candidate book-type nibble in the payload), and store the exact bytes as
/// a <see cref="BookTypeRecipe"/> that the Windows Devices layer can replay
/// verbatim on that drive. Nothing here is invented; the recipe is the drive's
/// own command, and re-emitting it reproduces the capture byte-for-byte.
/// </summary>
public static class BookTypeBitsetting
{
    public sealed record Finding(
        int Index,
        string CommandName,
        bool LooksLikeBitsetting,
        BookType? CandidateBookType,
        string Explanation);

    /// <summary>Decode what is publicly knowable about one captured command.</summary>
    public static Finding Analyze(MmcCommand cmd, int index)
    {
        switch (cmd.Opcode)
        {
            case 0xBF:      // SEND DISC/DVD STRUCTURE — the usual book-type carrier
            {
                int format = cmd.Cdb.Length > 7 ? cmd.Cdb[7] : -1;
                BookType? candidate = null;
                string payloadNote = "no DATA-OUT captured";
                if (cmd.DataOut.Length >= 5)
                {
                    // Structure payload follows a 4-byte header (length + reserved);
                    // the book type is conventionally the high nibble of its first byte.
                    candidate = BookTypes.FromNibble(cmd.DataOut[4] >> 4);
                    payloadNote = $"payload[0]=0x{cmd.DataOut[4]:X2} → candidate book type " +
                                  $"{candidate.Value.Name()} (high nibble)";
                }
                return new Finding(index, "SEND DISC STRUCTURE", true, candidate,
                    $"Book-type command (format 0x{(format < 0 ? 0 : format):X2}); {payloadNote}. " +
                    "The exact meaning is the drive's — captured for verbatim replay, not interpreted further.");
            }

            case 0x55 or 0x15:      // MODE SELECT(10)/(6) — some drives use a vendor page
            {
                int pageCode = ModeSelectFirstPage(cmd);
                bool vendor = pageCode >= 0x20;      // vendor-specific page range
                return new Finding(index, cmd.OpcodeName, vendor, null,
                    pageCode < 0
                        ? "MODE SELECT with no readable page — captured for replay."
                        : $"MODE SELECT of page 0x{pageCode:X2}" +
                          (vendor ? " (vendor-specific — a candidate bitsetting page); captured for replay."
                                  : " (standard page; unlikely to be bitsetting)."));
            }

            case >= 0xE0:           // vendor-specific opcode
                return new Finding(index, cmd.OpcodeName, true, null,
                    "Vendor-specific command — a candidate bitsetting op; captured for verbatim replay.");

            default:
                return new Finding(index, cmd.OpcodeName, false, null,
                    "Not a recognised book-type command shape.");
        }
    }

    public static IReadOnlyList<Finding> AnalyzeAll(IReadOnlyList<MmcCommand> commands)
    {
        var list = new List<Finding>(commands.Count);
        for (int i = 0; i < commands.Count; i++) list.Add(Analyze(commands[i], i));
        return list;
    }

    /// <summary>Page code of the first mode page in a MODE SELECT parameter list,
    /// or -1 if it can't be located. Skips the mode header + block descriptors.</summary>
    private static int ModeSelectFirstPage(MmcCommand cmd)
    {
        var d = cmd.DataOut;
        if (cmd.Opcode == 0x55)   // MODE SELECT(10): 8-byte header, BD length at [6..7]
        {
            if (d.Length < 8) return -1;
            int bd = (d[6] << 8) | d[7];
            int off = 8 + bd;
            return off < d.Length ? d[off] & 0x3F : -1;
        }
        // MODE SELECT(6): 4-byte header, BD length at [3]
        if (d.Length < 4) return -1;
        int bd6 = d[3];
        int off6 = 4 + bd6;
        return off6 < d.Length ? d[off6] & 0x3F : -1;
    }
}

/// <summary>
/// A book-type command learned from a drive trace: the exact CDB and DATA-OUT
/// bytes the drive was given, tagged with which drive and what it was doing.
/// Re-emitting it reproduces the captured command byte-for-byte; the Windows
/// Devices layer can later issue it over SPTI to that drive. DiscForge stores the
/// drive's own command — it never synthesises book-type bytes.
/// </summary>
public sealed record BookTypeRecipe(
    string? DriveVendor,
    string? DriveModel,
    string? Label,
    BookType? Target,
    byte[] Cdb,
    byte[] DataOut)
{
    public static BookTypeRecipe Learn(MmcCommand cmd, string? vendor, string? model,
                                       string? label, BookType? target)
        => new(vendor, model, label, target, cmd.Cdb, cmd.DataOut);

    public MmcCommand ToCommand() => new(Cdb, DataOut, "out");

    public string ToJson()
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append("\"driveVendor\":").Append(Str(DriveVendor)).Append(',');
        sb.Append("\"driveModel\":").Append(Str(DriveModel)).Append(',');
        sb.Append("\"label\":").Append(Str(Label)).Append(',');
        sb.Append("\"target\":").Append(Str(Target?.Name())).Append(',');
        sb.Append("\"opcode\":").Append(Str($"0x{(Cdb.Length > 0 ? Cdb[0] : 0):X2}")).Append(',');
        sb.Append("\"cdb\":").Append(Str(MmcTrace.Hex(Cdb))).Append(',');
        sb.Append("\"dataOut\":").Append(Str(MmcTrace.Hex(DataOut)));
        sb.Append('}');
        return sb.ToString();
    }

    public static BookTypeRecipe FromJson(string json)
    {
        string? vendor = Field(json, "driveVendor");
        string? model = Field(json, "driveModel");
        string? label = Field(json, "label");
        string? targetName = Field(json, "target");
        byte[] cdb = ParseHex(Field(json, "cdb") ?? "");
        byte[] data = ParseHex(Field(json, "dataOut") ?? "");
        BookType? target = targetName is null ? null : BookTypes.Parse(targetName);
        return new BookTypeRecipe(vendor, model, label, target, cdb, data);
    }

    // ---- tiny JSON helpers (flat object, string values) --------------------

    private static string Str(string? s) => s is null ? "null"
        : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string? Field(string json, string key)
    {
        int k = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (k < 0) return null;
        int colon = json.IndexOf(':', k + key.Length + 2);
        if (colon < 0) return null;
        int i = colon + 1;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        if (i < json.Length && json[i] == 'n') return null;         // null
        if (i >= json.Length || json[i] != '"') return null;
        var sb = new StringBuilder();
        for (i++; i < json.Length && json[i] != '"'; i++)
        {
            if (json[i] == '\\' && i + 1 < json.Length) { i++; sb.Append(json[i] == 'n' ? '\n' : json[i]); }
            else sb.Append(json[i]);
        }
        return sb.ToString();
    }

    private static byte[] ParseHex(string s)
    {
        var r = MmcTrace.Parse("CDB: " + s);
        return r.Commands.Count > 0 ? r.Commands[0].Cdb : Array.Empty<byte>();
    }
}
