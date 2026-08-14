# Bitsetting (book-type change) — the clean-room, provable path

"Bitsetting" changes a recordable DVD's **book type** (disc category) field —
most usefully setting a DVD+R / +R DL to **DVD-ROM** so a fussy set-top player
treats it as a pressed disc. It is the last feature ImgBurn has that DiscForge
does not, and for a good reason: the command that *sets* the book type is
**vendor-specific** (LG, Pioneer, Lite-On/Sony, NEC and Samsung each differ),
and those bytes are not in any public standard.

DiscForge's rule is *provably correct or declined*. Fabricating vendor command
bytes we cannot validate would break that rule, so DiscForge **does not ship
guessed bitsetting commands**. Instead it takes the honest, provable route:
**learn the command from your own drive and replay it verbatim.**

## How it works

1. **Capture** the command your drive actually issues. Run a tool that already
   does bitsetting on your writer (e.g. ImgBurn, or the drive vendor's utility)
   while a SCSI/MMC bus or SPTI sniffer records the traffic. You are capturing
   *your own hardware's* behaviour — no third-party source is copied.
2. **Save** the capture as a small text trace:

   ```
   # DVD+R → DVD-ROM on an LG BH16
   CDB:  BF 00 00 00 00 00 00 A1 00 04 00 00
   DATA: 00 02 00 00 00 00 00 00
   ```

   `CDB:` is the command descriptor block; `DATA:` is any data-out payload;
   commands are separated by a blank line. Hex may use spaces, commas or `0x`.
3. **Decode and learn:**

   ```
   dforge booktype-trace lg.mmctrace --vendor HL-DT-ST --model BH16NS55 \
                         --target DVD-ROM --save recipe.json
   ```

   DiscForge decodes what is *publicly* knowable — the opcode
   (`SEND DISC STRUCTURE`, a vendor `MODE SELECT` page, or a vendor opcode), the
   obvious CDB fields, and the candidate book-type nibble in the payload — and
   flags the bitsetting-shaped command. With `--save` it stores the drive's
   **exact** CDB and DATA-OUT bytes as a replay recipe, tagged with the drive and
   what it does.
4. **Replay (on hardware).** The recipe reproduces the captured command
   byte-for-byte; the Windows `DiscForge.Devices` layer can issue it over SPTI to
   that drive. This is the only hardware-bound step.

## What is proven vs. what needs the drive

- **Proven in CI** (`BookTypeBitsettingTests`): the trace parser, the honest
  analyzer (opcode names, field decode, candidate book type), and the round-trip
  that guarantees a learned recipe re-emits the captured bytes exactly and
  survives a JSON save/load.
- **Needs the drive**: capturing the trace (your hardware) and issuing the
  replay over SPTI. DiscForge supplies everything except the vendor bytes, which
  by design come only from your capture.

## Why this is the right design

It is genuinely clean-room — nothing is copied from ImgBurn or a vendor tool;
DiscForge observes commands your own drive issues. It is provable — the recipe
is validated against the capture, not guessed. And it still closes the gap: once
you have captured the command for a drive, DiscForge can set the book type on
that drive as reliably as the tool you captured, with a recipe you can inspect,
version and share.

Reading the *current* book type (from a disc's physical format information) is a
standard, non-vendor operation and is safe to do directly; only *setting* it is
vendor-specific and therefore learned rather than fabricated.
