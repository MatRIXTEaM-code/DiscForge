// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Cdg;

/// <summary>A decoded CD+G frame: an 8-bit RGBA bitmap, top-left origin,
/// 4 bytes per pixel. The full 300×216 plane is exposed (visible area is
/// 294×204 inside a 6-pixel border); the caller decides how much to crop.</summary>
public sealed class CdgImage
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Row-major RGBA, length Width × Height × 4.</summary>
    public byte[] Rgba { get; }

    public CdgImage(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        Rgba = rgba ?? throw new ArgumentNullException(nameof(rgba));
    }
}
