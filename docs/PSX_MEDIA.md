# PlayStation media codecs — ADX, VAB/SEQ, STR (and the MDEC deferral)

Task #110 adds three clean-room media pieces. Two decode fully and are validated
here; the third (STR) demuxes fully, with the MDEC *pixel* decode deferred under
the project's **provably correct or declined** rule — the same bar as ECM/RVZ.

## Shipped and validated

| Piece | Status |
|-------|--------|
| **ADX (CRI) ADPCM → WAV** | **Full decode.** Header parse + standard type 0x02/0x03 ADPCM, mono and stereo, validated against the documented predictor math recomputed independently in the tests. |
| **VAB (VAG bank)** | **Full structure parse.** Header, 128 program headers, per-program 16×32 tone attributes, 256-entry VAG pointer table (offsets = running sum × 8). Validated with hand-built banks. |
| **SEQ (sequence)** | **Full structure parse.** Header (version, ppqn, tempo) + a MIDI-like event walk (VLQ delta times, running status, 0xFF meta, end-of-track). Event count validated against hand-built sequences. |
| **STR demux** | **Full demux.** Splits Mode 2 Form 1 sectors into XA-audio (pass-through) and video, reassembles each frame's MDEC bitstream from its chunks (in index order, trimmed to the declared byte length), and reports width/height/frame number. Validated with hand-built STR sectors. |

## Shipped: MDEC frame → pixels (version 2)

`MdecFrameDecoder.DecodeFrame(bitstream, width, height)` now turns a demuxed STR
**version 2** frame into an RGBA image, wired into `dforge str-frames <in.str>
<out-dir>` (one `frame_NNNNN.png` per frame). The pipeline:

1. a **16-bit little-endian, MSB-first** bit reader (the console's DMA order),
2. the **DC** (10-bit signed) and **AC run/level** variable-length codes — the
   standard MPEG-1 intra table (Table B-14) with a trailing sign bit, plus the
   `000001` escape (6-bit run + 10-bit signed level) and the `10` end-of-block,
3. **dequantize** against the PSX quantization matrix × the frame `qscale` —
   applied only to coefficients the stream actually emits, so absent coefficients
   stay exactly zero (the `+4/8` rounding must never bias a flat region),
4. the existing **8×8 inverse DCT**, then **YCbCr 4:2:0 → RGB**, assembling the
   six blocks (Cr, Cb, Y1..Y4) into 16×16 macroblocks in the console's
   column-major order.

### How it is validated (oracle-free, from the spec)

There is no reference-frame oracle in the offline build, so the tests validate
every part that the spec pins directly:

- the AC VLC decode against hand-built codes taken straight from Table B-14
  (`11`→run0/level1, `0101`→run2/level−1, the escape, the EOB), and the table is
  asserted **prefix-free** at load;
- the 16-bit-LE / MSB-first bit reader;
- **full DC-only frames** — which are legitimate MDEC bitstreams — decoding to the
  exact flat colours the DC values, IDCT and colour matrix predict (a black frame
  is a uniform 128/128/128; a luma-DC frame is a uniform brighter grey; a Cr-DC
  frame tints red), plus non-multiple-of-16 clipping.

This proves the bit packing, DC path, block assembly, macroblock placement,
clipping and colour end-to-end. It caught a real bug during development: the shared
`Mdec.Dequantize` applies `(c·q·scale+4)/8.0` to all 64 slots, turning *absent* AC
coefficients into `0.5` and biasing flat regions — the decoder now dequantizes only
the emitted coefficients.

### Honest caveat (interop)

Full pixel-exact agreement with the console/`jPSXdec` on **arbitrary real game
frames** is not yet regression-tested against a reference clip (none is available
in this environment), and the `+4/8` step uses float division where the hardware
uses an integer shift (at most a ±1 rounding difference per sample). **Version 3**
(differential DC with separate luma/chroma tables) is detected and reported, not
mis-decoded.

The interop check is already wired: point the `DFORGE_FIXTURES` environment variable at
a directory containing `mdec/reference.str` (a real PlayStation STR video), and
`InteropFixtureTests.Mdec_decodes_every_frame_of_a_reference_str_without_error` decodes
every frame — a real STR running end-to-end without hitting "invalid AC code" is strong
evidence the Table B-14 VLC and bit order are right. The test is inert with no fixture.

`dforge str-demux` still writes each frame's raw MDEC bitstream and reports the
XA-audio sectors, for callers that want the pre-decode data.
