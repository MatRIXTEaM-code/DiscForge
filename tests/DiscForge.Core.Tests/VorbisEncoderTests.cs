// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Audio;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The clean-room Ogg Vorbis encoder. There is no in-process Vorbis decoder to round-
/// trip against (unlike FLAC), so this verifies the output the strongest way that
/// needs no external tool: it re-parses the Ogg container and the Vorbis headers the
/// encoder produced and holds them to the specification — every page's CRC checks,
/// the stream is framed correctly (BOS then headers then audio then EOS), the three
/// header packets carry the right type bytes and "vorbis" signature, the
/// identification header decodes back to the channel count, sample rate and 2048-
/// sample blocksizes, and the final granule equals the number of input samples.
/// A malformed bitstream — the real risk in a hand-rolled encoder — fails these.
/// (End-to-end decode by libvorbis/ffmpeg is verified out-of-band during development.)
/// </summary>
public class VorbisEncoderTests
{
    private static short[] Tone(int frames, int channels)
    {
        var s = new short[frames * channels];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++)
                s[i * channels + c] = (short)(9000 * Math.Sin(i * (0.05 + 0.02 * c)));
        return s;
    }

    // ---- an independent Ogg page reader for the test ------------------------

    private sealed record Page(byte Header, long Granule, uint Serial, uint Seq, byte[] Body, bool CrcOk);

    private static List<Page> ReadPages(byte[] data)
    {
        var pages = new List<Page>();
        int p = 0;
        while (p + 27 <= data.Length)
        {
            Assert.True(data[p] == 'O' && data[p + 1] == 'g' && data[p + 2] == 'g' && data[p + 3] == 'S',
                "page did not start with the OggS capture pattern");
            Assert.Equal(0, data[p + 4]);                         // stream structure version
            byte header = data[p + 5];
            long granule = BitConverter.ToInt64(data, p + 6);
            uint serial = BitConverter.ToUInt32(data, p + 14);
            uint seq = BitConverter.ToUInt32(data, p + 18);
            uint storedCrc = BitConverter.ToUInt32(data, p + 22);
            int nsegs = data[p + 26];
            int segTable = p + 27;
            int bodyLen = 0;
            for (int i = 0; i < nsegs; i++) bodyLen += data[segTable + i];
            int bodyStart = segTable + nsegs;
            int pageLen = bodyStart + bodyLen - p;

            // recompute CRC over the whole page with the CRC field zeroed
            var page = new byte[pageLen];
            Array.Copy(data, p, page, 0, pageLen);
            page[22] = page[23] = page[24] = page[25] = 0;
            bool crcOk = OggCrc(page) == storedCrc;

            var body = new byte[bodyLen];
            Array.Copy(data, bodyStart, body, 0, bodyLen);
            pages.Add(new Page(header, granule, serial, seq, body, crcOk));
            p = bodyStart + bodyLen;
        }
        return pages;
    }

    private static uint OggCrc(byte[] d)
    {
        uint c = 0;
        foreach (byte b in d) c = (c << 8) ^ Table[((c >> 24) & 0xFF) ^ b];
        return c;
    }
    private static readonly uint[] Table = BuildTable();
    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i << 24;
            for (int j = 0; j < 8; j++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04c11db7 : r << 1;
            t[i] = r;
        }
        return t;
    }

    // LSB-first bit reader over a packet, to decode the identification header.
    private sealed class Bits
    {
        private readonly byte[] _d; private int _pos;
        public Bits(byte[] d) { _d = d; }
        public uint Read(int n)
        {
            uint v = 0;
            for (int i = 0; i < n; i++)
            {
                int bit = (_d[_pos >> 3] >> (_pos & 7)) & 1;
                v |= (uint)bit << i; _pos++;
            }
            return v;
        }
    }

    // ---- tests --------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Output_is_a_well_framed_ogg_stream(int channels)
    {
        int frames = 44100;                        // 1 second
        var pcm = Tone(frames, channels);
        var ogg = VorbisEncoder.Encode(pcm, 44100, channels);

        var pages = ReadPages(ogg);
        Assert.True(pages.Count >= 3, "expected at least identification, headers and audio pages");
        Assert.All(pages, pg => Assert.True(pg.CrcOk, "an Ogg page CRC did not validate"));

        Assert.Equal(0x02, pages[0].Header & 0x02);        // first page is BOS
        Assert.Equal(0x04, pages[^1].Header & 0x04);       // last page is EOS

        // all pages share one serial number and ascend in sequence
        uint serial = pages[0].Serial;
        for (int i = 0; i < pages.Count; i++)
        {
            Assert.Equal(serial, pages[i].Serial);
            Assert.Equal((uint)i, pages[i].Seq);
        }

        // final granule == input sample count (per channel)
        Assert.Equal(frames, pages[^1].Granule);
    }

    [Theory]
    [InlineData(1, 44100)]
    [InlineData(2, 44100)]
    [InlineData(2, 22050)]
    public void Identification_header_round_trips_the_stream_parameters(int channels, int rate)
    {
        var pcm = Tone(4096, channels);
        var ogg = VorbisEncoder.Encode(pcm, rate, channels);
        var pages = ReadPages(ogg);

        // The identification packet is the entire body of the BOS page.
        byte[] ident = pages[0].Body;
        Assert.Equal(1, ident[0]);                          // packet type 1
        Assert.Equal("vorbis", System.Text.Encoding.ASCII.GetString(ident, 1, 6));

        var bits = new Bits(ident);
        bits.Read(8);                                       // packet type
        for (int i = 0; i < 6; i++) bits.Read(8);           // "vorbis"
        Assert.Equal(0u, bits.Read(32));                    // vorbis_version
        Assert.Equal((uint)channels, bits.Read(8));
        Assert.Equal((uint)rate, bits.Read(32));
        bits.Read(32); bits.Read(32); bits.Read(32);        // bitrate max/nom/min
        Assert.Equal(11u, bits.Read(4));                    // blocksize_0 => 2^11
        Assert.Equal(11u, bits.Read(4));                    // blocksize_1 => 2^11
        Assert.Equal(1u, bits.Read(1));                     // framing bit
    }

    [Fact]
    public void Comment_and_setup_headers_carry_the_right_type_bytes()
    {
        var ogg = VorbisEncoder.Encode(Tone(4096, 2), 44100, 2);
        var pages = ReadPages(ogg);

        // Page 1 holds the comment (type 3) then setup (type 5) packets, both
        // starting with the "vorbis" signature. It begins with the comment type byte.
        byte[] body = pages[1].Body;
        Assert.Equal(3, body[0]);
        Assert.Equal("vorbis", System.Text.Encoding.ASCII.GetString(body, 1, 6));
        // The setup header (type 5) appears later in the same page body.
        bool sawSetup = false;
        for (int i = 0; i + 7 <= body.Length; i++)
            if (body[i] == 5 && System.Text.Encoding.ASCII.GetString(body, i + 1, 6) == "vorbis")
            { sawSetup = true; break; }
        Assert.True(sawSetup, "setup header (type 5, 'vorbis') was not found");
    }

    [Fact]
    public void High_quality_produces_a_larger_stream_than_standard()
    {
        var pcm = Tone(44100, 2);
        int std = VorbisEncoder.Encode(pcm, 44100, 2, VorbisEncoder.Quality.Standard).Length;
        int high = VorbisEncoder.Encode(pcm, 44100, 2, VorbisEncoder.Quality.High).Length;
        Assert.True(high > std, $"High ({high}) should exceed Standard ({std}) for the same audio");
    }

    [Fact]
    public void Rejects_unsupported_channel_counts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VorbisEncoder.Encode(new short[300], 44100, 3));
    }
}
