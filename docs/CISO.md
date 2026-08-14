# CSO / ZSO (compressed ISO)

CSO (`.cso`) and ZSO (`.zso`) are block-compressed ISO images — the format OPL,
PSP/PS2 loaders and ROM libraries use to store disc images smaller. Unlike CHD
(whose full extraction is a large multi-codec job), CSO/ZSO is simple, so DiscForge
supports it **fully**: decompress either back to a plain ISO it can inspect and
patch, and compress an ISO to CSO for storage.

```
dforge ciso-info    game.cso            # kind, sizes, compression ratio
dforge ciso-to-iso  game.cso game.iso   # decompress CSO or ZSO -> ISO
dforge iso-to-ciso  game.iso game.cso   # compress ISO -> CSO (zlib)
```

## The format (clean-room, from the public CISO/ZISO description)

A 24-byte little-endian header (magic `CISO` for zlib or `ZISO` for LZ4, the
uncompressed size, and a 2048-byte block size), then an index of `blocks + 1`
32-bit offsets. Bit 31 of an index entry marks a block stored raw; the low bits
(shifted by the header's alignment) are its file offset, and block *N* spans
`index[N]..index[N+1]`. Compressed blocks are raw deflate (CSO) or an LZ4 block
(ZSO). A block that wouldn't shrink is stored raw, so the output is never larger
than the source per block.

The LZ4 block decoder is implemented clean-room from the public LZ4 block format
(a token of literal/match lengths, the literals, a little-endian back-offset, and
the match).

## Validated

The CSO path is validated by round trip — an ISO compressed to CSO and
decompressed back is byte-identical — across compressible data, incompressible
data (which is stored raw), and a non-block-aligned final block. The LZ4 decoder is
pinned with hand-built blocks whose literals-and-match output is known. Both the
CLI and the streamed core handle full-size DVD images without loading them whole.

## Scope

Read/decompress: CSO (zlib) and ZSO (LZ4). Compress: CSO (zlib). ZSO *compression*
(LZ4 encode) is the one piece not yet done — decompressing ZSO works, but DiscForge
writes CSO when compressing. Nothing here is protection-related.
