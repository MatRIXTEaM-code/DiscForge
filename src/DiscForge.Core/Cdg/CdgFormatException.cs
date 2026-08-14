// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Cdg;

/// <summary>Thrown when CD+G input is malformed in a way that matters
/// (a genuinely broken container, not merely a short or empty stream —
/// those decode to an empty picture).</summary>
public sealed class CdgFormatException : Exception
{
    public CdgFormatException(string message) : base(message) { }
    public CdgFormatException(string message, Exception inner) : base(message, inner) { }
}
