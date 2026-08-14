// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Util;

namespace DiscForge.Core.Cdg;

/// <summary>Convenience helpers to render a <c>.cdg</c> stream to a frame.</summary>
public static class CdgRenderer
{
    /// <summary>Decode <paramref name="cdg"/> up to time <paramref name="t"/> and
    /// return the RGBA frame. A negative time renders the blank initial state;
    /// a time past the end renders the final frame.</summary>
    public static CdgImage RenderFrameAt(byte[] cdg, TimeSpan t)
    {
        ArgumentNullException.ThrowIfNull(cdg);
        var file = CdgFile.Parse(cdg);
        var decoder = new CdgDecoder(file.Packets);
        decoder.ApplyAtTime(t);
        return decoder.Render();
    }

    /// <summary>Render the final frame of <paramref name="cdg"/> (all packets
    /// applied).</summary>
    public static CdgImage RenderFinalFrame(byte[] cdg)
    {
        ArgumentNullException.ThrowIfNull(cdg);
        var file = CdgFile.Parse(cdg);
        var decoder = new CdgDecoder(file.Packets);
        decoder.ApplyThrough(file.Packets.Count);
        return decoder.Render();
    }

    /// <summary>Render the frame at time <paramref name="t"/> to a PNG.</summary>
    public static byte[] RenderToPng(byte[] cdg, TimeSpan t)
    {
        var image = RenderFrameAt(cdg, t);
        return PngWriter.EncodeRgba(image.Rgba, image.Width, image.Height);
    }

    /// <summary>Encode an already-decoded frame to a PNG.</summary>
    public static byte[] RenderToPng(CdgImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return PngWriter.EncodeRgba(image.Rgba, image.Width, image.Height);
    }
}
