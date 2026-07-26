// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Cue;
using DiscForge.Core.Util;

namespace DiscForge.Core.Raw;

/// <summary>Control field bits of the Q sub-channel (Red Book §22).</summary>
[Flags]
public enum QControl : byte
{
    None = 0,
    /// <summary>Audio with pre-emphasis (audio tracks only).</summary>
    PreEmphasis = 0x1,
    /// <summary>Digital copy permitted.</summary>
    CopyPermitted = 0x2,
    /// <summary>Data track.</summary>
    Data = 0x4,
    /// <summary>Four-channel audio.</summary>
    FourChannel = 0x8,
}

/// <summary>
/// One sector's worth of sub-channel data in logical form: the P pause flag,
/// the 12-byte formatted Q frame (CRC included), and 96 six-bit R–W symbols.
/// The three physical layouts drives accept are emitted from this one model:
///
///   PQ-16 (2368/sector): the MMC "formatted Q" — Q's 12 bytes, three pad
///     bytes, and P as bit 7 of the final byte.
///   Packed / "cooked" (2448): eight channels laid out consecutively,
///     12 bytes each: P, Q, then R..W as bit-planes of the symbols.
///   Interleaved / raw (2448): byte i carries bit i of every channel —
///     P in bit 7, Q in bit 6, symbol i in bits 5..0.
///
/// Emitting all three from one source means the layouts can be property-tested
/// against each other (pack ↔ interleave must describe the same channels).
/// </summary>
public sealed class SubcodeFrame
{
    public bool P;
    public byte[] Q = new byte[12];
    /// <summary>96 six-bit symbols (values 0..63): CD-TEXT / CD+G pack data.</summary>
    public byte[] Rw = new byte[96];

    public const int SizePq16 = 16;
    public const int Size96 = 96;

    public void EmitPq16(Span<byte> dst)
    {
        Q.CopyTo(dst[..12]);
        dst[12] = dst[13] = dst[14] = 0;
        dst[15] = (byte)(P ? 0x80 : 0x00);
    }

    public void EmitPacked96(Span<byte> dst)
    {
        dst[..96].Clear();
        // P: one flag repeated over all 96 bits.
        for (int i = 0; i < 12; i++) dst[i] = (byte)(P ? 0xFF : 0x00);
        // Q: the frame verbatim.
        Q.CopyTo(dst.Slice(12, 12));
        // R..W: bit-plane b of symbol i → channel (R+b) byte i/8, bit 7-(i%8).
        for (int i = 0; i < 96; i++)
        {
            byte sym = Rw[i];
            for (int ch = 0; ch < 6; ch++)
                if ((sym & (1 << (5 - ch))) != 0)
                    dst[12 * (2 + ch) + (i >> 3)] |= (byte)(0x80 >> (i & 7));
        }
    }

    public void EmitInterleaved96(Span<byte> dst)
    {
        for (int i = 0; i < 96; i++)
        {
            int b = (Rw[i] & 0x3F);
            if (P) b |= 0x80;
            if ((Q[i >> 3] & (0x80 >> (i & 7))) != 0) b |= 0x40;
            dst[i] = (byte)b;
        }
    }

    // ---- parsing (the emitters, run backwards — used by the inspector) -----

    /// <summary>Extract the 12-byte Q frame from a sector's subcode.</summary>
    public static void ExtractQ(ReadOnlySpan<byte> sub, RawSubcodeForm form, Span<byte> q12)
    {
        switch (form)
        {
            case RawSubcodeForm.Pq16:
                sub[..12].CopyTo(q12);
                break;
            case RawSubcodeForm.Packed96:
                sub.Slice(12, 12).CopyTo(q12);
                break;
            case RawSubcodeForm.Interleaved96:
                q12.Clear();
                for (int i = 0; i < 96; i++)
                    if ((sub[i] & 0x40) != 0)
                        q12[i >> 3] |= (byte)(0x80 >> (i & 7));
                break;
        }
    }

    /// <summary>Extract the 96 six-bit R–W symbols from a sector's subcode.
    /// PQ-16 carries none; the result is all zeros.</summary>
    public static void ExtractRw(ReadOnlySpan<byte> sub, RawSubcodeForm form, Span<byte> rw96)
    {
        rw96.Clear();
        switch (form)
        {
            case RawSubcodeForm.Packed96:
                for (int i = 0; i < 96; i++)
                {
                    int sym = 0;
                    for (int ch = 0; ch < 6; ch++)
                        if ((sub[12 * (2 + ch) + (i >> 3)] & (0x80 >> (i & 7))) != 0)
                            sym |= 1 << (5 - ch);
                    rw96[i] = (byte)sym;
                }
                break;
            case RawSubcodeForm.Interleaved96:
                for (int i = 0; i < 96; i++) rw96[i] = (byte)(sub[i] & 0x3F);
                break;
        }
    }
}

/// <summary>
/// Builds the 12-byte formatted Q frames: lead-in TOC entries, program-area
/// position frames (with the pregap countdown), the media catalog number
/// (ADR 2) and per-track ISRC (ADR 3). CRC-16 is always appended inverted.
/// </summary>
public static class SubQ
{
    // ---- frame skeleton ----------------------------------------------------

    private static byte[] Frame(QControl control, int adr, Action<byte[]> fill)
    {
        var q = new byte[12];
        q[0] = (byte)(((byte)control << 4) | (adr & 0x0F));
        fill(q);
        ushort crc = Crc16.ComputeInverted(q.AsSpan(0, 10));
        q[10] = (byte)(crc >> 8);
        q[11] = (byte)crc;
        return q;
    }

    // ---- lead-in (TOC) -----------------------------------------------------

    /// <summary>
    /// A lead-in TOC entry: TNO=00, POINT = track number or A0/A1/A2, running
    /// lead-in time in MIN/SEC/FRAME, and the point's target in PMIN/PSEC/PFRAME.
    /// </summary>
    public static byte[] LeadInToc(QControl control, byte point, Msf runningTime, Msf pointTarget)
        => Frame(control, adr: 1, q =>
        {
            q[1] = 0x00;                       // TNO: lead-in
            q[2] = point;                      // POINT (BCD track, or A0/A1/A2)
            q[3] = Bcd.From(runningTime.Minutes);
            q[4] = Bcd.From(runningTime.Seconds);
            q[5] = Bcd.From(runningTime.Frames);
            q[6] = 0x00;
            q[7] = Bcd.From(pointTarget.Minutes);
            q[8] = Bcd.From(pointTarget.Seconds);
            q[9] = Bcd.From(pointTarget.Frames);
        });

    /// <summary>A0 entry: PMIN = first track number, PSEC = disc type.</summary>
    public static byte[] LeadInA0(QControl control, Msf runningTime, int firstTrack, byte discType)
        => Frame(control, adr: 1, q =>
        {
            q[1] = 0x00; q[2] = 0xA0;
            q[3] = Bcd.From(runningTime.Minutes);
            q[4] = Bcd.From(runningTime.Seconds);
            q[5] = Bcd.From(runningTime.Frames);
            q[6] = 0x00;
            q[7] = Bcd.From(firstTrack);
            q[8] = discType;                   // 00 = CD-DA / Mode 1, 20 = XA
            q[9] = 0x00;
        });

    // ---- program area ------------------------------------------------------

    /// <summary>
    /// A program-area position frame. In a pregap (index 0) the relative time
    /// counts DOWN to 00:00:00 at the start of index 1; elsewhere it counts up
    /// from the start of index 1.
    /// </summary>
    public static byte[] Position(QControl control, int track, int index, Msf relative, Msf absolute)
        => Frame(control, adr: 1, q =>
        {
            q[1] = Bcd.From(track);
            q[2] = Bcd.From(index);
            q[3] = Bcd.From(relative.Minutes);
            q[4] = Bcd.From(relative.Seconds);
            q[5] = Bcd.From(relative.Frames);
            q[6] = 0x00;
            q[7] = Bcd.From(absolute.Minutes);
            q[8] = Bcd.From(absolute.Seconds);
            q[9] = Bcd.From(absolute.Frames);
        });

    // ---- MCN (ADR 2) -------------------------------------------------------

    /// <summary>
    /// Media catalog number frame: 13 BCD digits packed high-nibble first into
    /// bytes 1..7 (final nibble zero), AFRAME in byte 9.
    /// </summary>
    public static byte[] Mcn(QControl control, string catalog13Digits, Msf absolute)
        => Frame(control, adr: 2, q =>
        {
            if (catalog13Digits.Length != 13 || !catalog13Digits.All(char.IsAsciiDigit))
                throw new ArgumentException("An MCN is exactly 13 decimal digits.", nameof(catalog13Digits));
            for (int d = 0; d < 13; d++)
            {
                int nibble = catalog13Digits[d] - '0';
                int pos = 1 + d / 2;
                q[pos] |= (byte)((d & 1) == 0 ? nibble << 4 : nibble);
            }
            q[8] = 0x00;
            q[9] = Bcd.From(absolute.Frames);
        });

    // ---- ISRC (ADR 3) ------------------------------------------------------

    /// <summary>
    /// ISRC frame: five 6-bit alphanumerics (country + owner) packed into
    /// bytes 1..4, seven BCD digits (year + designation) in bytes 5..8,
    /// AFRAME in byte 9. Six-bit code = ASCII − 0x30.
    /// </summary>
    public static byte[] Isrc(QControl control, string isrc12, Msf absolute)
        => Frame(control, adr: 3, q =>
        {
            if (isrc12.Length != 12)
                throw new ArgumentException("An ISRC is exactly 12 characters.", nameof(isrc12));
            Span<byte> six = stackalloc byte[5];
            for (int i = 0; i < 5; i++)
            {
                char c = char.ToUpperInvariant(isrc12[i]);
                if (c is not ((>= '0' and <= '9') or (>= 'A' and <= 'Z')))
                    throw new ArgumentException($"ISRC character '{c}' is not alphanumeric.");
                six[i] = (byte)(c - 0x30);
            }
            for (int i = 5; i < 12; i++)
                if (!char.IsAsciiDigit(isrc12[i]))
                    throw new ArgumentException("ISRC positions 6..12 are digits.");

            q[1] = (byte)((six[0] << 2) | (six[1] >> 4));
            q[2] = (byte)((six[1] << 4) | (six[2] >> 2));
            q[3] = (byte)((six[2] << 6) | six[3]);
            q[4] = (byte)(six[4] << 2);
            q[5] = (byte)(((isrc12[5] - '0') << 4) | (isrc12[6] - '0'));
            q[6] = (byte)(((isrc12[7] - '0') << 4) | (isrc12[8] - '0'));
            q[7] = (byte)(((isrc12[9] - '0') << 4) | (isrc12[10] - '0'));
            q[8] = (byte)((isrc12[11] - '0') << 4);
            q[9] = Bcd.From(absolute.Frames);
        });
}
