#!/usr/bin/env python3
"""
Alcohol 120% MDS/MDF format — reference oracle.

An Alcohol image is a pair:
  - .mds  Media Descriptor: header, session blocks, track blocks, extra blocks,
          footers and filenames. Small.
  - .mdf  Media Data File: the raw track data, referenced by offset from the MDS.

Layout below is from public format documentation (libmirage's mds parser and the
long-circulated MDS format notes). No Alcohol binary was disassembled, and no
Alcohol-produced file was inspected to derive it.

HEADER (88 bytes / 0x58)
  0x00  char[16]  "MEDIA DESCRIPTOR"
  0x10  uint8[2]  version (major, minor)
  0x12  uint16    medium type
  0x14  uint16    session count
  0x16  uint16[2] (unused)
  0x1A  uint16    BCA length
  0x1C  uint32[2] (unused)
  0x24  uint32    BCA offset
  0x28  uint32[6] (unused)
  0x40  uint32    disc structures offset
  0x44  uint32[3] (unused)
  0x50  uint32    session blocks offset
  0x54  uint32    DPM blocks offset

SESSION BLOCK (24 bytes / 0x18)
  0x00  int32     session start (LBA, may be negative for the 150-sector pregap)
  0x04  int32     session end (LBA)
  0x08  uint16    session number
  0x0A  uint8     total blocks in this session (incl. non-track)
  0x0B  uint8     non-track blocks (the A0/A1/A2 lead-in descriptors)
  0x0C  uint16    first track
  0x0E  uint16    last track
  0x10  uint32    (unused)
  0x14  uint32    track blocks offset

TRACK BLOCK (80 bytes / 0x50)
  0x00  uint8     mode
  0x01  uint8     subchannel mode
  0x02  uint8     ADR/CONTROL  (adr<<4 | control)
  0x03  uint8     TNO
  0x04  uint8     point  (1..99 = track; 0xA0/A1/A2 = lead-in descriptors)
  0x05  uint8[3]  min/sec/frame
  0x08  uint8     zero
  0x09  uint8[3]  pmin/psec/pframe
  0x0C  uint32    extra block offset
  0x10  uint16    sector size
  0x12  uint8[18] (unused)
  0x24  uint32    start sector (LBA)
  0x28  uint64    start offset (into the MDF)
  0x30  uint32    number of filenames
  0x34  uint32    footer offset
  0x38  uint8[24] (unused)

TRACK EXTRA BLOCK (8 bytes)
  0x00  uint32    pregap sectors
  0x04  uint32    length in sectors

FOOTER (16 bytes)
  0x00  uint32    filename offset
  0x04  uint32    wide-char filename flag
  0x08  uint32[2] (unused)

The lead-in descriptors (points 0xA0/0xA1/0xA2) carry first track, last track and
lead-out position in their pmin/psec/pframe fields as MSF — NOT as an LBA. That
is the fiddly part and the main reason to prove this before porting.
"""
import struct

SIGNATURE = b"MEDIA DESCRIPTOR"

# Track modes
MODE_NONE = 0x00
MODE_AUDIO = 0xA9
MODE_MODE1 = 0xAA
MODE_MODE2 = 0xAB
MODE_MODE2_FORM1 = 0xAC
MODE_MODE2_FORM2 = 0xAD

# Subchannel
SUB_NONE = 0x00
SUB_PW_INTERLEAVED = 0x08

# Medium types
MEDIUM_CD = 0x00
MEDIUM_CDR = 0x01
MEDIUM_CDRW = 0x02
MEDIUM_DVD = 0x10
MEDIUM_DVDR = 0x12

HEADER_SIZE = 0x58
SESSION_SIZE = 0x18
TRACK_SIZE = 0x50
EXTRA_SIZE = 8
FOOTER_SIZE = 16


def lba_to_msf(lba):
    """CD addressing: LBA 0 == 00:02:00, so the 150-sector offset is added."""
    lba += 150
    return (lba // (60 * 75), (lba // 75) % 60, lba % 75)


def msf_to_lba(m, s, f):
    return (m * 60 + s) * 75 + f - 150


def build(tracks, medium=MEDIUM_CD, session_start=-150):
    """
    tracks: list of dicts {point, mode, sector_size, lba, length, pregap, control}
    Returns (mds_bytes, layout_dict) — layout is for assertions in the self-test.
    """
    real = [t for t in tracks if 1 <= t["point"] <= 99]
    first_track = min(t["point"] for t in real)
    last_track = max(t["point"] for t in real)
    lead_out = max(t["lba"] + t["length"] for t in real)

    # Lead-in descriptors carry first/last/lead-out as MSF in pmin/psec/pframe.
    non_track = [
        {"point": 0xA0, "pmsf": (first_track, 0, 0)},
        {"point": 0xA1, "pmsf": (last_track, 0, 0)},
        {"point": 0xA2, "pmsf": lba_to_msf(lead_out)},
    ]

    n_all = len(non_track) + len(real)

    # ---- offsets ----
    sessions_off = HEADER_SIZE
    tracks_off = sessions_off + SESSION_SIZE
    extras_off = tracks_off + n_all * TRACK_SIZE
    footers_off = extras_off + len(real) * EXTRA_SIZE
    names_off = footers_off + FOOTER_SIZE

    # ---- header ----
    hdr = bytearray(HEADER_SIZE)
    hdr[0:16] = SIGNATURE
    hdr[0x10] = 1          # version major
    hdr[0x11] = 3          # version minor
    struct.pack_into("<H", hdr, 0x12, medium)
    struct.pack_into("<H", hdr, 0x14, 1)          # one session
    struct.pack_into("<I", hdr, 0x50, sessions_off)

    # ---- session block ----
    ses = bytearray(SESSION_SIZE)
    struct.pack_into("<i", ses, 0x00, session_start)
    struct.pack_into("<i", ses, 0x04, lead_out)
    struct.pack_into("<H", ses, 0x08, 1)
    ses[0x0A] = n_all
    ses[0x0B] = len(non_track)
    struct.pack_into("<H", ses, 0x0C, first_track)
    struct.pack_into("<H", ses, 0x0E, last_track)
    struct.pack_into("<I", ses, 0x14, tracks_off)

    # ---- track blocks ----
    blocks = bytearray()
    mdf_offset = 0
    extra_i = 0
    layout = {"tracks": []}

    for nt in non_track:
        b = bytearray(TRACK_SIZE)
        b[0x00] = MODE_NONE
        b[0x02] = 0x10          # ADR=1, CONTROL=0
        b[0x04] = nt["point"]
        pm, ps, pf = nt["pmsf"]
        b[0x09], b[0x0A], b[0x0B] = pm, ps, pf
        blocks += b

    for t in real:
        b = bytearray(TRACK_SIZE)
        b[0x00] = t["mode"]
        b[0x01] = SUB_NONE
        b[0x02] = (1 << 4) | (t.get("control", 0x04) & 0x0F)
        b[0x03] = 0
        b[0x04] = t["point"]
        m, s, f = lba_to_msf(t["lba"])
        b[0x05], b[0x06], b[0x07] = m, s, f
        struct.pack_into("<I", b, 0x0C, extras_off + extra_i * EXTRA_SIZE)
        struct.pack_into("<H", b, 0x10, t["sector_size"])
        struct.pack_into("<I", b, 0x24, t["lba"])
        struct.pack_into("<Q", b, 0x28, mdf_offset)
        struct.pack_into("<I", b, 0x30, 1)                 # one filename
        struct.pack_into("<I", b, 0x34, footers_off)
        blocks += b

        stored = (t["pregap"] + t["length"]) * t["sector_size"]
        layout["tracks"].append({
            "point": t["point"], "mdf_offset": mdf_offset,
            "stored": stored, "sector_size": t["sector_size"],
            "lba": t["lba"], "length": t["length"], "pregap": t["pregap"],
        })
        mdf_offset += stored
        extra_i += 1

    # ---- extra blocks ----
    extras = bytearray()
    for t in real:
        e = bytearray(EXTRA_SIZE)
        struct.pack_into("<I", e, 0x00, t["pregap"])
        struct.pack_into("<I", e, 0x04, t["length"])
        extras += e

    # ---- footer + filename ----
    foot = bytearray(FOOTER_SIZE)
    struct.pack_into("<I", foot, 0x00, names_off)
    struct.pack_into("<I", foot, 0x04, 0)      # not wide-char

    name = b"*.mdf\x00"

    mds = bytes(hdr) + bytes(ses) + bytes(blocks) + bytes(extras) + bytes(foot) + name
    layout["mdf_total"] = mdf_offset
    return mds, layout


def parse(mds):
    if len(mds) < HEADER_SIZE:
        raise ValueError("MDS too short for a header")
    if mds[0:16] != SIGNATURE:
        raise ValueError("not an MDS file (bad signature)")

    ver = (mds[0x10], mds[0x11])
    medium = struct.unpack_from("<H", mds, 0x12)[0]
    n_sessions = struct.unpack_from("<H", mds, 0x14)[0]
    sessions_off = struct.unpack_from("<I", mds, 0x50)[0]

    sessions = []
    for i in range(n_sessions):
        off = sessions_off + i * SESSION_SIZE
        if off + SESSION_SIZE > len(mds):
            raise ValueError("session block past end of file")
        start = struct.unpack_from("<i", mds, off + 0x00)[0]
        end = struct.unpack_from("<i", mds, off + 0x04)[0]
        number = struct.unpack_from("<H", mds, off + 0x08)[0]
        n_all = mds[off + 0x0A]
        n_non = mds[off + 0x0B]
        first_t = struct.unpack_from("<H", mds, off + 0x0C)[0]
        last_t = struct.unpack_from("<H", mds, off + 0x0E)[0]
        tracks_off = struct.unpack_from("<I", mds, off + 0x14)[0]

        tracks = []
        lead_out_lba = None
        for j in range(n_all):
            toff = tracks_off + j * TRACK_SIZE
            if toff + TRACK_SIZE > len(mds):
                raise ValueError("track block past end of file")
            mode = mds[toff + 0x00]
            subch = mds[toff + 0x01]
            adr_ctl = mds[toff + 0x02]
            point = mds[toff + 0x04]
            pmin, psec, pframe = mds[toff + 0x09], mds[toff + 0x0A], mds[toff + 0x0B]
            extra_off = struct.unpack_from("<I", mds, toff + 0x0C)[0]
            sector_size = struct.unpack_from("<H", mds, toff + 0x10)[0]
            start_sector = struct.unpack_from("<I", mds, toff + 0x24)[0]
            start_offset = struct.unpack_from("<Q", mds, toff + 0x28)[0]

            if point == 0xA2:
                lead_out_lba = msf_to_lba(pmin, psec, pframe)
            if not (1 <= point <= 99):
                continue     # lead-in descriptor, not real track data

            pregap = length = 0
            if extra_off and extra_off + EXTRA_SIZE <= len(mds):
                pregap = struct.unpack_from("<I", mds, extra_off + 0x00)[0]
                length = struct.unpack_from("<I", mds, extra_off + 0x04)[0]

            tracks.append({
                "point": point, "mode": mode, "subchannel": subch,
                "adr": (adr_ctl >> 4) & 0x0F, "control": adr_ctl & 0x0F,
                "sector_size": sector_size, "lba": start_sector,
                "mdf_offset": start_offset, "pregap": pregap, "length": length,
                "is_audio": mode == MODE_AUDIO,
            })

        sessions.append({"number": number, "start": start, "end": end,
                         "first_track": first_t, "last_track": last_t,
                         "lead_out": lead_out_lba, "tracks": tracks})

    return {"version": ver, "medium": medium, "sessions": sessions}


if __name__ == "__main__":
    ok = True

    # --- Case 1: single data track ---
    mds, layout = build([
        {"point": 1, "mode": MODE_MODE1, "sector_size": 2048,
         "lba": 0, "length": 1000, "pregap": 0, "control": 0x04},
    ])
    p = parse(mds)
    s = p["sessions"][0]
    t = s["tracks"][0]
    c1 = (p["version"] == (1, 3) and len(s["tracks"]) == 1
          and t["sector_size"] == 2048 and t["length"] == 1000
          and t["mdf_offset"] == 0 and s["lead_out"] == 1000
          and not t["is_audio"])
    print(f"  single data track          : {'OK' if c1 else 'FAIL'}  "
          f"lead_out={s['lead_out']} len={t['length']}")
    ok &= c1

    # --- Case 2: audio CD, MDF offsets must accumulate ---
    mds, layout = build([
        {"point": 1, "mode": MODE_AUDIO, "sector_size": 2352,
         "lba": 0, "length": 500, "pregap": 150, "control": 0x00},
        {"point": 2, "mode": MODE_AUDIO, "sector_size": 2352,
         "lba": 500, "length": 700, "pregap": 0, "control": 0x00},
    ])
    p = parse(mds)
    ts = p["sessions"][0]["tracks"]
    exp1 = (150 + 500) * 2352
    c2 = (len(ts) == 2 and all(x["is_audio"] for x in ts)
          and ts[0]["mdf_offset"] == 0 and ts[1]["mdf_offset"] == exp1
          and ts[0]["pregap"] == 150 and ts[1]["length"] == 700)
    print(f"  audio, MDF offsets accrue  : {'OK' if c2 else 'FAIL'}  "
          f"t2_offset={ts[1]['mdf_offset']} expected={exp1}")
    ok &= c2

    # --- Case 3: mixed mode; lead-out survives the MSF round trip ---
    mds, layout = build([
        {"point": 1, "mode": MODE_MODE1, "sector_size": 2048,
         "lba": 0, "length": 30000, "pregap": 0, "control": 0x04},
        {"point": 2, "mode": MODE_AUDIO, "sector_size": 2352,
         "lba": 30000, "length": 20000, "pregap": 150, "control": 0x00},
    ])
    p = parse(mds)
    s = p["sessions"][0]
    c3 = (s["lead_out"] == 50000 and s["first_track"] == 1 and s["last_track"] == 2
          and s["tracks"][0]["control"] == 0x04 and s["tracks"][1]["control"] == 0x00)
    print(f"  mixed mode + MSF lead-out  : {'OK' if c3 else 'FAIL'}  lead_out={s['lead_out']}")
    ok &= c3

    # --- Case 4: MSF round trip across the whole CD range ---
    bad = [lba for lba in (0, 1, 74, 75, 4499, 4500, 150000, 333000)
           if msf_to_lba(*lba_to_msf(lba)) != lba]
    print(f"  LBA<->MSF round trip       : {'OK' if not bad else f'FAIL {bad}'}")
    ok &= not bad

    # --- Case 5: rubbish must be rejected ---
    for label, blob in [("empty", b""), ("short", b"MEDIA"),
                        ("bad signature", b"NOT A DESCRIPTOR" + bytes(200))]:
        try:
            parse(blob)
            print(f"  reject {label:20}: FAIL (accepted)")
            ok = False
        except ValueError:
            print(f"  reject {label:20}: OK")

    print("\nMDS FORMAT:", "PASS" if ok else "FAIL")
