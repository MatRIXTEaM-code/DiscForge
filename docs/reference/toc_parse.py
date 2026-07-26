#!/usr/bin/env python3
"""
MMC READ TOC/PMA/ATIP (opcode 0x43) response parsing — reference oracle.

Format 0 (TOC) response layout, per MMC:
    bytes 0-1 : TOC Data Length (big-endian), counts everything AFTER these 2 bytes
    byte  2   : First Track Number
    byte  3   : Last Track Number
    then N descriptors of 8 bytes each:
        byte 0 : reserved
        byte 1 : ADR (high nibble) | CONTROL (low nibble)
        byte 2 : Track Number   (0xAA == lead-out)
        byte 3 : reserved
        bytes 4-7 : Track Start Address (LBA, big-endian, when MSF=0)

CONTROL bit 2 (0x04) distinguishes a data track from audio — this is what tells
us whether a track is a CD-DA track or a data track, and therefore what sector
size to read it at.

Track lengths are NOT in the TOC: each track runs to the start of the next one,
and the last runs to the lead-out. That derivation is the fiddly part and the
reason this is worth proving before porting.
"""
import struct

LEADOUT = 0xAA


def build_toc(first_track, tracks, leadout_lba):
    """tracks: list of (number, adr, control, start_lba). Builds a format-0 response."""
    descs = b""
    for (num, adr, control, lba) in tracks:
        descs += bytes([0, (adr << 4) | (control & 0x0F), num, 0]) + struct.pack(">I", lba)
    descs += bytes([0, (1 << 4) | 0x04, LEADOUT, 0]) + struct.pack(">I", leadout_lba)

    last_track = max(t[0] for t in tracks)
    body = bytes([first_track, last_track]) + descs
    return struct.pack(">H", len(body)) + body


def parse_toc(resp):
    data_len = struct.unpack(">H", resp[0:2])[0]
    total = data_len + 2
    if total > len(resp):
        raise ValueError(f"TOC truncated: header says {total} bytes, got {len(resp)}")

    first, last = resp[2], resp[3]
    n = (data_len - 2) // 8
    entries = []
    for i in range(n):
        off = 4 + i * 8
        b1 = resp[off + 1]
        adr = (b1 >> 4) & 0x0F
        control = b1 & 0x0F
        num = resp[off + 2]
        lba = struct.unpack(">I", resp[off + 4:off + 8])[0]
        entries.append({"number": num, "adr": adr, "control": control, "lba": lba,
                        "is_data": bool(control & 0x04)})

    leadout = next((e["lba"] for e in entries if e["number"] == LEADOUT), None)
    if leadout is None:
        raise ValueError("TOC has no lead-out entry")

    tracks = [e for e in entries if e["number"] != LEADOUT]
    tracks.sort(key=lambda e: e["number"])

    # Derive lengths: each track runs to the next track's start; last to lead-out.
    for i, t in enumerate(tracks):
        end = tracks[i + 1]["lba"] if i + 1 < len(tracks) else leadout
        t["length"] = end - t["lba"]

    return {"first": first, "last": last, "leadout": leadout, "tracks": tracks}


if __name__ == "__main__":
    ok = True

    # --- Case 1: single data track (a typical data CD) ---
    resp = build_toc(1, [(1, 1, 0x04, 0)], leadout_lba=333000)
    t = parse_toc(resp)
    c1 = (t["first"] == 1 and t["last"] == 1 and len(t["tracks"]) == 1
          and t["tracks"][0]["is_data"] and t["tracks"][0]["length"] == 333000)
    print(f"  single data track           : {'OK' if c1 else 'FAIL'}  "
          f"len={t['tracks'][0]['length']}")
    ok &= c1

    # --- Case 2: audio CD, 3 tracks ---
    resp = build_toc(1, [(1, 1, 0x00, 0), (2, 1, 0x00, 20000), (3, 1, 0x00, 45000)],
                     leadout_lba=70000)
    t = parse_toc(resp)
    lengths = [x["length"] for x in t["tracks"]]
    c2 = (lengths == [20000, 25000, 25000] and all(not x["is_data"] for x in t["tracks"]))
    print(f"  audio CD, 3 tracks          : {'OK' if c2 else 'FAIL'}  lengths={lengths}")
    ok &= c2

    # --- Case 3: mixed mode (data track 1, audio 2-3) ---
    resp = build_toc(1, [(1, 1, 0x04, 0), (2, 1, 0x00, 30000), (3, 1, 0x00, 50000)],
                     leadout_lba=60000)
    t = parse_toc(resp)
    flags = [x["is_data"] for x in t["tracks"]]
    lengths = [x["length"] for x in t["tracks"]]
    c3 = (flags == [True, False, False] and lengths == [30000, 20000, 10000])
    print(f"  mixed mode (data + audio)   : {'OK' if c3 else 'FAIL'}  "
          f"data={flags} lengths={lengths}")
    ok &= c3

    # --- Case 4: truncated response must be rejected, not silently misread ---
    resp = build_toc(1, [(1, 1, 0x04, 0)], leadout_lba=1000)
    try:
        parse_toc(resp[:-4])
        print("  truncated response          : FAIL (accepted)")
        ok = False
    except ValueError:
        print("  truncated response          : OK (rejected)")

    # --- Case 5: pre-emphasis / copy bits must not be mistaken for the data bit ---
    resp = build_toc(1, [(1, 1, 0x01, 0), (2, 1, 0x02, 10000)], leadout_lba=20000)
    t = parse_toc(resp)
    c5 = all(not x["is_data"] for x in t["tracks"])
    print(f"  pre-emphasis/copy != data   : {'OK' if c5 else 'FAIL'}")
    ok &= c5

    print("\nTOC PARSER:", "PASS" if ok else "FAIL")
