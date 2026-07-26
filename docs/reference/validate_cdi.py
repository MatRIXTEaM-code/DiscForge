#!/usr/bin/env python3
"""
Reference CDI validator — the throwaway Python oracle used to confirm the
C# parser's format understanding against a REAL image before trusting it.

This script was run against tests/fixtures/cdi4dc_audiodata_v35.cdi (a genuine
CDI produced by cdi4dc 0.3b from a known-contents ISO) and every assertion
below PASSED. It exists so the findings are reproducible and so the same
oracle can be pointed at future images (esp. real DiscJuggler-authored ones).

It is NOT part of the shipping product — DiscForge.Core is the real parser.
This is scaffolding/documentation of empirically confirmed format facts.

Usage:  python3 validate_cdi.py <image.cdi> [<source.iso>]
"""
import sys, struct

VER = {0x80000004: "V2", 0x80000005: "V3", 0x80000006: "V35"}


def read_trailer(data: bytes):
    magic, locator = struct.unpack("<II", data[-8:])
    ver = VER.get(magic, "UNKNOWN")
    if ver == "UNKNOWN":
        raise ValueError(f"unknown magic 0x{magic:08X}")
    # v3.5+ : locator is descriptor LENGTH from EOF. v2/v3 : absolute offset.
    desc_off = (len(data) - locator) if ver == "V35" else locator
    return ver, magic, locator, desc_off


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    data = open(sys.argv[1], "rb").read()
    ver, magic, locator, desc_off = read_trailer(data)

    print(f"file size        : {len(data)}")
    print(f"version magic    : 0x{magic:08X} ({ver})")
    print(f"locator          : {locator}")
    print(f"descriptor offset: {desc_off} (len {len(data) - desc_off})")

    d = data[desc_off:-8]
    n_sessions = struct.unpack_from("<H", d, 0)[0]
    print(f"sessions         : {n_sessions}")

    # --- CONFIRMED format facts (cdi4dc 0.3b, v3.5 Audio/Data) --------------
    # session[0] track count at offset 2
    n_tracks0 = struct.unpack_from("<H", d, 2)[0]
    assert n_tracks0 == 1, n_tracks0

    # 4-byte lead-in (00 00 00 00), THEN two 10-byte start marks.
    # (Correction vs first-draft spec, which put a conditional 0x80000000
    #  dword here instead of a plain 4-byte lead-in.)
    MARK = bytes([0, 0, 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF])
    assert d[8:18] == MARK and d[18:28] == MARK, "track start mark mismatch"

    # 4 unknown bytes, then filename-length u8, then filename.
    fn_len = d[0x20]
    fn = d[0x21:0x21 + fn_len].decode("ascii", "replace")
    print(f"track0 filename  : {fn!r} (len {fn_len})")

    # --- Data-track storage model (CONFIRMED to the byte) -------------------
    # Data track stored as Mode2/Form1 2336-byte sectors, user data at +8.
    # Verified: user_byte(sec s, intra o) = BASE + s*2336 + 8 + o
    if len(sys.argv) >= 3:
        iso = open(sys.argv[2], "rb").read()
        checks = {
            "PVD 'CD001'": iso.find(b"CD001"),
            "MARKER.DAT":  iso.find(b"0123456789ABCDEF"),
            "README":      iso.find(b"DiscForge CDI parser validation"),
        }
        # Solve BASE from the first check, verify the rest predict exactly.
        (name0, ip0) = next(iter(checks.items()))
        cp0 = data.find(iso[ip0:ip0 + 16])
        s0, o0 = divmod(ip0, 2048)
        base = cp0 - (s0 * 2336 + 8 + o0)
        print(f"data track base  : {base}")
        ok = True
        for name, ip in checks.items():
            cp = data.find(iso[ip:ip + 16])
            s, o = divmod(ip, 2048)
            pred = base + s * 2336 + 8 + o
            good = pred == cp
            ok &= good
            print(f"  {name:14} predicted {pred:>8}  actual {cp:>8}  {'OK' if good else 'MISMATCH'}")
        print("STORAGE MODEL:", "CONFIRMED" if ok else "FAILED")

    print("\nAll structural assertions passed.")


if __name__ == "__main__":
    main()
