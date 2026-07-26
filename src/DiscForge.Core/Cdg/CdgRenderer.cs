// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

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
