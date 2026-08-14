// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using DiscForge.Core.Cheat;

namespace DiscForge.App.Views;

/// <summary>
/// The cheat-code workbench: decode a Game Genie (NES / SNES / Genesis / Game Boy)
/// or GameShark (PS1) code into the raw address / value / compare it encodes, and —
/// for the Game Genie families — encode an address+value back into a code. This is
/// format translation only: DiscForge reads and writes the published Game Genie /
/// GameShark encodings, it does not *generate* new cheats. A thin shell over
/// <see cref="GameGenie"/> and <see cref="GameShark"/>.
/// </summary>
internal sealed class CheatView : UserControl
{
    private static readonly (string Label, CheatPlatform Platform, bool GameShark)[] DecodePlatforms =
    {
        ("NES (Game Genie)", CheatPlatform.Nes, false),
        ("SNES (Game Genie)", CheatPlatform.Snes, false),
        ("Genesis (Game Genie)", CheatPlatform.Genesis, false),
        ("Game Boy (Game Genie)", CheatPlatform.GameBoy, false),
        ("PS1 (GameShark)", CheatPlatform.GameSharkPs1, true),
    };

    private static readonly (string Label, CheatPlatform Platform)[] EncodePlatforms =
    {
        ("NES", CheatPlatform.Nes),
        ("SNES", CheatPlatform.Snes),
        ("Genesis", CheatPlatform.Genesis),
        ("Game Boy", CheatPlatform.GameBoy),
    };

    private readonly ComboBox _decPlatform = new() { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(90, 40), Width = 200, Font = Theme.Ui };
    private readonly TextBox _code = new() { Location = new Point(90, 70), Width = 300, Font = Theme.Mono };
    private readonly TextBox _decResult = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Theme.Mono,
        Location = new Point(12, 108), Size = new Size(690, 96), BackColor = Color.White,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };

    private readonly ComboBox _encPlatform = new() { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(90, 40), Width = 160, Font = Theme.Ui };
    private readonly TextBox _addr = new() { Location = new Point(90, 70), Width = 110, Font = Theme.Mono };
    private readonly TextBox _value = new() { Location = new Point(280, 70), Width = 90, Font = Theme.Mono };
    private readonly TextBox _compare = new() { Location = new Point(470, 70), Width = 90, Font = Theme.Mono };
    private readonly TextBox _encResult = new()
    {
        ReadOnly = true, Font = Theme.Mono, Location = new Point(90, 108), Width = 400, BackColor = Color.White,
    };

    public CheatView()
    {
        Size = new Size(736, 452);
        BackColor = Color.White;
        Padding = new Padding(12);

        foreach (var p in DecodePlatforms) _decPlatform.Items.Add(p.Label);
        _decPlatform.SelectedIndex = 0;
        foreach (var p in EncodePlatforms) _encPlatform.Items.Add(p.Label);
        _encPlatform.SelectedIndex = 0;

        var decodeBox = new GroupBox { Text = "Decode a code", Location = new Point(12, 6), Size = new Size(710, 216), Font = Theme.UiBold };
        decodeBox.Controls.Add(new Label { Text = "Platform:", AutoSize = true, Location = new Point(12, 43), Font = Theme.Ui });
        decodeBox.Controls.Add(new Label { Text = "Code:", AutoSize = true, Location = new Point(12, 73), Font = Theme.Ui });
        var decodeBtn = new Button { Text = "Decode", Location = new Point(400, 68), Width = 90, FlatStyle = FlatStyle.System };
        decodeBtn.Click += (_, _) => Decode();
        _code.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { Decode(); e.SuppressKeyPress = true; } };
        decodeBox.Controls.AddRange(new Control[] { _decPlatform, _code, decodeBtn, _decResult });

        var encodeBox = new GroupBox { Text = "Encode a Game Genie code (hex)", Location = new Point(12, 230), Size = new Size(710, 156), Font = Theme.UiBold };
        encodeBox.Controls.Add(new Label { Text = "Platform:", AutoSize = true, Location = new Point(12, 43), Font = Theme.Ui });
        encodeBox.Controls.Add(new Label { Text = "Address:", AutoSize = true, Location = new Point(12, 73), Font = Theme.Ui });
        encodeBox.Controls.Add(new Label { Text = "Value:", AutoSize = true, Location = new Point(220, 73), Font = Theme.Ui });
        encodeBox.Controls.Add(new Label { Text = "Compare:", AutoSize = true, Location = new Point(400, 73), Font = Theme.Ui });
        encodeBox.Controls.Add(new Label { Text = "(optional)", AutoSize = true, Location = new Point(470, 92), Font = Theme.Ui, ForeColor = Color.Gray });
        encodeBox.Controls.Add(new Label { Text = "Code:", AutoSize = true, Location = new Point(12, 111), Font = Theme.Ui });
        var encodeBtn = new Button { Text = "Encode", Location = new Point(580, 66), Width = 90, FlatStyle = FlatStyle.System };
        encodeBtn.Click += (_, _) => Encode();
        encodeBox.Controls.AddRange(new Control[] { _encPlatform, _addr, _value, _compare, encodeBtn, _encResult });

        Controls.Add(decodeBox);
        Controls.Add(encodeBox);
    }

    private void Decode()
    {
        var (_, platform, gameShark) = DecodePlatforms[_decPlatform.SelectedIndex];
        string code = _code.Text.Trim();
        if (code.Length == 0) { _decResult.Text = "Enter a code to decode."; return; }
        try
        {
            var c = gameShark ? GameShark.Parse(code, platform) : GameGenie.Decode(platform, code);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Platform:  {c.Platform}");
            sb.AppendLine($"Address:   0x{c.Address:X}");
            sb.AppendLine($"Value:     0x{c.Value:X}");
            sb.AppendLine(c.Compare is { } cmp ? $"Compare:   0x{cmp:X}" : "Compare:   (none)");
            if (!string.IsNullOrEmpty(c.Description)) sb.AppendLine($"Note:      {c.Description}");
            _decResult.Text = sb.ToString().TrimEnd();
            StatusBus.Report($"Decoded {c.Platform} code");
        }
        catch (CheatFormatException ex) { _decResult.Text = "Error: " + ex.Message; }
        catch (Exception ex) { _decResult.Text = "Error: " + ex.Message; AppLog.WriteException("cheat-decode", ex); }
    }

    private void Encode()
    {
        var (_, platform) = EncodePlatforms[_encPlatform.SelectedIndex];
        if (!TryHex(_addr.Text, out long address)) { _encResult.Text = "Bad hex address."; return; }
        if (!TryHex(_value.Text, out long value)) { _encResult.Text = "Bad hex value."; return; }
        long? compare = null;
        if (_compare.Text.Trim().Length > 0)
        {
            if (!TryHex(_compare.Text, out long cmp)) { _encResult.Text = "Bad hex compare."; return; }
            compare = cmp;
        }
        try
        {
            var code = new CheatCode { Platform = platform, Address = address, Value = value, Compare = compare };
            _encResult.Text = GameGenie.Encode(code);
            StatusBus.Report($"Encoded {platform} Game Genie code");
        }
        catch (CheatFormatException ex) { _encResult.Text = "Error: " + ex.Message; }
        catch (Exception ex) { _encResult.Text = "Error: " + ex.Message; AppLog.WriteException("cheat-encode", ex); }
    }

    private static bool TryHex(string s, out long value)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
