# PlayStation PAL / NTSC video-mode conversion

The PAL4U / Zapper 2000 job, done the clean-room way and kept strictly to what it
says on the tin: **video-mode (display timing) conversion only.** DiscForge changes
the PS1 GPU's display-mode bit so a game outputs 60 Hz (NTSC) or 50 Hz (PAL). It
does **not** touch region codes, copy protection, or cheat codes — and in
particular it does **not** generate "Y-Fix" vertical-centring codes, which are
Pro-Action-Replay cheat codes and stay outside the clean-room boundary
(docs/COMPARISON.md §13).

## How it works

The PlayStation GPU selects its display mode with GP1 command 0x08. The 32-bit
command word is `0x08000000 | param`, and the low byte's **bit 3** is the video
mode: 0 = NTSC/60 Hz, 1 = PAL/50 Hz. The rest of the parameter is resolution,
colour depth and interlace, which a conversion must leave untouched.

Converting a game is therefore: find the display-mode command words in its
executable or disc image, and flip bit 3 — preserving every other bit. DiscForge
scans at every byte offset (a literal command can sit unaligned in data), and only
reports sites whose current mode differs from the target, so an already-correct
command is left alone.

```
dforge psx-video-mode game.bin --to ntsc                    # list the sites it would change
dforge psx-video-mode game.bin --to ntsc --ppf patch.ppf    # emit an undoable PPF
dforge psx-video-mode game.bin --to pal  --out game_pal.bin # write the converted image
```

The PPF path reuses DiscForge's own PPF 3.0 engine, so the result is a standard,
undoable, validated patch any PPF tool (PPF-O-Matic, the PPF Patch Engine) can
apply — and DiscForge can revert.

## Validated

Round-trip through the PPF engine: a buffer with a PAL display-mode command is
converted, the generated PPF is applied back, and the result must equal the
directly-patched image. The GP1(08h) recognition, the bit-3-only change (resolution
and interlace bits preserved), and the scanner's "only sites that need converting"
behaviour are each pinned by tests.

## Honest scope — what this does and doesn't do

- **Does:** flip the video-mode bit of every literal GP1(08h) display-mode command
  it finds, in a PS-EXE or a disc image, and emit a PPF or a converted image.
- **Doesn't:** handle a game that computes the mode word dynamically (no literal to
  find), compensate for the frame-rate/speed change a mode switch causes, or
  re-centre the picture. Those are game-specific; the speed and centring fixes in
  particular are the province of per-game PAR codes, which DiscForge does not
  generate. A converted game may run faster/slower or sit off-centre, exactly as
  the mechanical-conversion tools' output does before a game-specific fix.
- **Never:** changes region markers, defeats protection, or writes cheat codes.

This is the mechanical, preservation-safe core of PAL/NTSC conversion — the part
that is unambiguously format work rather than circumvention.
