// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.App;

/// <summary>
/// Tiny decoupling bus so views can report status to the shell's status bar
/// without holding a reference to MainForm. MainForm subscribes; views report.
/// </summary>
internal static class StatusBus
{
    public static event Action<string>? Changed;

    public static void Report(string message) => Changed?.Invoke(message);
}
