// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;

namespace DiscForge.Core.Util;

/// <summary>
/// Emit a binary file as source text — the "WinBin2Src" job: a C byte array or a
/// GNU-assembler <c>.byte</c> block, for pasting a small blob (a header, a lookup
/// table, a boot logo) straight into code. Pure and deterministic.
/// </summary>
public static class BinToSource
{
    /// <summary>A C array: <c>const unsigned char name[LEN] = { 0x.., … };</c>.</summary>
    public static string ToCArray(ReadOnlySpan<byte> data, string name = "data", int perLine = 12)
    {
        if (perLine < 1) perLine = 12;
        var sb = new StringBuilder();
        sb.Append("const unsigned char ").Append(Sanitize(name))
          .Append('[').Append(data.Length).Append("] = {\n");
        for (int i = 0; i < data.Length; i++)
        {
            if (i % perLine == 0) sb.Append("    ");
            sb.Append("0x").Append(data[i].ToString("x2"));
            if (i != data.Length - 1) sb.Append(',');
            sb.Append((i % perLine == perLine - 1 || i == data.Length - 1) ? '\n' : ' ');
        }
        sb.Append("};\n");
        return sb.ToString();
    }

    /// <summary>A GNU-assembler block: a <c>name:</c> label then <c>.byte</c> rows.</summary>
    public static string ToAsm(ReadOnlySpan<byte> data, string name = "data", int perLine = 12)
    {
        if (perLine < 1) perLine = 12;
        var sb = new StringBuilder();
        sb.Append(Sanitize(name)).Append(":\n");
        for (int i = 0; i < data.Length; i += perLine)
        {
            sb.Append("    .byte ");
            int end = Math.Min(i + perLine, data.Length);
            for (int j = i; j < end; j++)
            {
                sb.Append("0x").Append(data[j].ToString("x2"));
                if (j != end - 1) sb.Append(", ");
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // A C/asm identifier: letters, digits, underscore; must not start with a digit.
    private static string Sanitize(string name)
    {
        if (string.IsNullOrEmpty(name)) return "data";
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        if (char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }
}
