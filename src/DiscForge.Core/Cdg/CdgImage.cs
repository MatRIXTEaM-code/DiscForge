// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

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
