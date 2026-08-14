// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System;
using System.IO;

/// <summary>
/// A write-only stream that turns DiscForge's combined interleaved raw output (2352-byte main
/// channel + 96-byte sub-channel per sector) into the two files a CloneCD set expects: the main
/// channel to a <c>.img</c> and the raw P-W sub-channel to a <c>.sub</c>. It first drops
/// <c>skipBytes</c> of lead-in (a CloneCD .img starts at program LBA 0), then routes each program
/// sector's 2352 main bytes to the image and its 96 sub bytes to the sub-channel file.
/// </summary>
internal sealed class CcdSplitStream : Stream
{
    private const int Main = 2352;
    private const int Sector = 2448;   // main + interleaved 96-byte sub

    private readonly Stream _img;
    private readonly Stream _sub;
    private long _skipRemaining;
    private long _programPos;

    public CcdSplitStream(Stream img, Stream sub, long skipBytes)
    {
        _img = img;
        _sub = sub;
        _skipRemaining = skipBytes;
    }

    public override bool CanWrite => true;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _programPos; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count)
    {
        int end = offset + count;
        while (offset < end)
        {
            if (_skipRemaining > 0)
            {
                int drop = (int)Math.Min(_skipRemaining, end - offset);
                _skipRemaining -= drop;
                offset += drop;
                continue;
            }

            int within = (int)(_programPos % Sector);
            int run;
            if (within < Main)
            {
                run = Math.Min(end - offset, Main - within);
                _img.Write(buffer, offset, run);
            }
            else
            {
                run = Math.Min(end - offset, Sector - within);
                _sub.Write(buffer, offset, run);
            }
            offset += run;
            _programPos += run;
        }
    }

    public override void Flush() { _img.Flush(); _sub.Flush(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
