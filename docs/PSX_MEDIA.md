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

## Deferred: MDEC frame → pixels

The STR demuxer hands back, per frame, the exact concatenated **MDEC bitstream**.
Turning that bitstream into an image requires the full MDEC pipeline:

1. **Huffman decode** the DC/AC coefficients (the DC/AC variable-length tables),
2. **dequantize** against the PSX quantization matrix scaled by the per-block
   `qscale`,
3. an **8×8 inverse DCT** per block,
4. **YUV 4:2:0 → RGB** using the console's specific colour-conversion constants and
   clamping, assembling 16×16 macroblocks.

### Why it is deferred (the honest reason)

Each of those stages has console-specific details that do not fail loudly when
wrong — they produce a *plausible but incorrect* image (shifted DC level, wrong
IDCT normalization, off-by-one chroma, or the wrong YUV→RGB matrix). There are
also multiple MDEC bitstream **versions** (v1/v2 vs v3) that encode the DC term
differently.

DiscForge's rule is that a codec ships only if it can be **validated against an
oracle** here. For MDEC that means one real STR frame with a known reference
decode (or an authoritative byte-level bitstream + pixel spec) so we can assert
our output matches the console byte-for-byte. A **self-round-trip** — encoding our
own minimal bitstream and decoding it back — would only prove the encoder and
decoder share the *same* assumptions; it would not prove interoperability with
real STR files, which is the entire point. That is exactly the ECM situation, and
we make the same call: ship the validated demux, defer the pixel decode.

### What finishing it needs (bounded, once unblocked)

1. One reference STR frame (bitstream + expected RGB, from any established PSX
   video tool), used as a fixture: `MdecDecode(reference.bitstream)` must equal
   `reference.rgb`. That single fixture pins the DC coding version, the quant
   matrix scaling, the IDCT normalization and the YUV→RGB constants at once.
2. `Mdec.DecodeFrame(bitstream, width, height)` over a validated 8×8 IDCT, wired
   into a `dforge str-frames <in.str> <out-dir>` PNG dump.
3. The reference-fixture test **plus** a self-consistency round trip, so both
   interop and internal consistency are locked in.

Until then, `dforge str-demux` writes each frame's raw MDEC bitstream plus a
summary, and reports the XA-audio sectors — everything that can be proven correct.
