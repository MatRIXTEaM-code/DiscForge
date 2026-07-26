// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;
using DiscForge.Core.Cdi;
using DiscForge.Core.Gdi;
using DiscForge.Core.Mds;
using DiscForge.Core.Nrg;
using Xunit;
using CueSheetT = DiscForge.Core.Cue.CueSheet;

namespace DiscForge.Core.Tests;

/// <summary>
/// The image/format parsers must decline hostile input — random bytes, truncations,
/// and magic-seeded garbage — with a domain exception, never a crash-type exception
/// (index/overflow/null) or a hang. A fuzz sweep of tens of thousands of mutations
/// confirms this; these lock in the behaviour and the specific out-of-range guards.
/// </summary>
public class ParserRobustnessTests
{
    // Acceptable = a parser saying "this isn't my format / it's malformed". Anything
    // else (IndexOutOfRange, Overflow, NullReference, OutOfMemory…) is a defect.
    private static bool IsGraceful(Exception e) =>
        e is GdiFormatException or CdiFormatException or NrgFormatException or MdsFormatException
          or IpBinFormatException or ArgumentException or FormatException
          or System.IO.EndOfStreamException or System.IO.InvalidDataException or NotSupportedException;

    private static void Parse(int which, byte[] d)
    {
        switch (which)
        {
            case 0: GdiParser.Parse(Encoding.Latin1.GetString(d)); break;
            case 1: CueSheetT.Parse(Encoding.Latin1.GetString(d)); break;
            case 2: CdiParser.Parse(new MemoryStream(d)); break;
            case 3: NrgParser.Parse(new MemoryStream(d)); break;
            case 4: MdsParser.Parse(d); break;
            case 5: if (d.Length >= 0x100) IpBin.Parse(d.AsSpan(0, 0x100)); break;
        }
    }

    [Fact]
    public void Every_parser_declines_random_and_magic_seeded_input_gracefully()
    {
        var rng = new Random(20260101);
        string[] magics = { "NER5", "NERO", "MEDIA DESCRIPTOR", "SEGA SEGAKATANA", "MComprHD", "3\n1 0 4 2352 x 0\n" };
        foreach (int sz in new[] { 0, 1, 8, 64, 256, 1024, 4096 })
        {
            for (int iter = 0; iter < 120; iter++)
            {
                var d = new byte[sz];
                rng.NextBytes(d);
                if (sz >= 16 && iter % 3 == 0)
                {
                    var mg = Encoding.ASCII.GetBytes(magics[rng.Next(magics.Length)]);
                    Array.Copy(mg, 0, d, 0, Math.Min(mg.Length, d.Length));
                    if (sz >= 16) Array.Copy(Encoding.ASCII.GetBytes("NER5"), 0, d, d.Length - 12, 4);
                }
                for (int which = 0; which < 6; which++)
                {
                    try { Parse(which, d); }
                    catch (Exception e)
                    {
                        Assert.True(IsGraceful(e), $"parser {which} on {sz} bytes threw {e.GetType().Name}: {e.Message}");
                    }
                }
            }
        }
    }

    [Fact]
    public void A_cdi_with_a_descriptor_offset_past_the_end_is_declined_not_crashed()
    {
        // Whatever the exact trailer values, a CDI whose descriptor offset lands past
        // the end of the file must be refused with a domain exception — never a
        // negative-length allocation (Overflow/ArgumentOutOfRange).
        var img = new byte[256];
        BitConverter.GetBytes(0x80000006u).CopyTo(img, img.Length - 8); // a plausible version word
        BitConverter.GetBytes(0x7FFFFFFFu).CopyTo(img, img.Length - 4); // descriptor offset well past the end
        Assert.Throws<CdiFormatException>(() => CdiParser.Parse(new MemoryStream(img)));
    }
}
