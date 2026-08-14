#!/usr/bin/env python3
"""
Reference parser for the DiscForge CANONICAL CDI layout (see gen_cdi.py).

Independent of the C# parser. Used to round-trip synthetic fixtures and to
serve as the executable spec the C# CdiParser canonical-path must match.
Prints the parsed structure and (with --verify-spec manifest.json) asserts the
parse matches the generator's intent exactly.
"""
import json, struct, sys

VER = {0x80000004: "v2", 0x80000005: "v3", 0x80000006: "v35"}
SECTOR_BYTES = {0: 2048, 1: 2336, 2: 2352}
MARK = bytes([0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF])


class R:
    def __init__(self, b): self.b, self.p = b, 0
    def take(self, n):
        if self.p + n > len(self.b):
            raise ValueError(f"underrun at {self.p} wanting {n}")
        s = self.b[self.p:self.p + n]; self.p += n; return s
    def u8(self): return self.take(1)[0]
    def u16(self): return struct.unpack("<H", self.take(2))[0]
    def u32(self): return struct.unpack("<I", self.take(4))[0]


def parse(data):
    magic, locator = struct.unpack("<II", data[-8:])
    ver = VER.get(magic)
    if not ver:
        raise ValueError(f"unknown magic 0x{magic:08X}")
    desc_off = (len(data) - locator) if ver == "v35" else locator
    if not (0 <= desc_off < len(data) - 8):
        raise ValueError("descriptor offset outside file")

    r = R(data[desc_off:-8])
    n_sessions = r.u16()
    sessions, running, idx = [], 0, 0
    for _ in range(n_sessions):
        n_tracks = r.u16()
        tracks = []
        for _ in range(n_tracks):
            lead = r.u32()                       # noqa: F841 (canonical 0)
            if r.take(10) != MARK or r.take(10) != MARK:
                raise ValueError(f"bad start mark at track {idx}")
            r.u32()                              # reserved0
            fn = r.take(r.u8()).decode("ascii", "replace")
            pregap = r.u32(); length = r.u32(); mode = r.u32()
            start_lba = r.u32(); total = r.u32(); ssc = r.u32()
            r.u32()                              # reserved1
            tracks.append(dict(index=idx, filename=fn, pregap=pregap,
                               length=length, mode=mode, start_lba=start_lba,
                               total=total, sector_size_code=ssc,
                               file_offset=running))
            running += SECTOR_BYTES[ssc] * total
            idx += 1
        r.u32()                                  # session tail
        sessions.append(tracks)
    return ver, sessions


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    data = open(sys.argv[1], "rb").read()
    ver, sessions = parse(data)
    print(f"{sys.argv[1]}: {ver}, {len(sessions)} session(s), "
          f"{sum(len(s) for s in sessions)} track(s)")
    for si, s in enumerate(sessions):
        for t in s:
            print(f"  S{si} T{t['index']} {['audio','mode1','mode2'][t['mode']]:5} "
                  f"{SECTOR_BYTES[t['sector_size_code']]}B pregap={t['pregap']} "
                  f"len={t['length']} lba={t['start_lba']} off={t['file_offset']} "
                  f"'{t['filename']}'")

    if len(sys.argv) >= 4 and sys.argv[2] == "--verify-spec":
        manifest = json.load(open(sys.argv[3]))
        name = sys.argv[1].split("/")[-1].removesuffix(".cdi")
        intent = manifest[name]["spec"]["sessions"]
        flat_intent = [t for sess in intent for t in sess]
        flat_got = [t for sess in sessions for t in sess]
        assert len(flat_intent) == len(flat_got), "track count mismatch"
        for want, got in zip(flat_intent, flat_got):
            for k in ("pregap", "length", "mode", "sector_size_code"):
                assert want[k] == got[k], f"{name}: {k} {want[k]}!={got[k]}"
            assert want["start_lba"] == got["start_lba"], f"{name}: lba"
        print(f"  SPEC MATCH: all fields round-trip correctly for {name}")


if __name__ == "__main__":
    main()
