#!/usr/bin/env python3
"""
Synthetic CDI generator — DiscForge CANONICAL layout.

Purpose: produce controlled, fully-specified CDI images to exercise the parser
across versions (v2/v3/v3.5) and topologies (multi-session, multi-track, mixed
modes and sector sizes) that the single real cdi4dc fixture cannot reach.

IMPORTANT — what these images do and do NOT prove:
  ✅ They exercise parser LOGIC: version dispatch, locator semantics, session/
     track enumeration, per-track file-offset accumulation, sector-size and
     mode decoding, and boundary/error handling.
  ❌ They do NOT prove byte-fidelity to real DiscJuggler output. This is the
     "DiscForge canonical" layout — a clean format WE define and WE write
     (see docs/CDI_FORMAT.md §"Canonical synthetic layout"). Real DiscJuggler
     descriptors are richer and remain future work pending a real DJ image.

This is an INDEPENDENT implementation of the same spec that C# CdiWriter
implements. Round-tripping Python-writes → C#-reads and C#-writes → Python-reads
cross-validates both against the written spec, not against each other's bugs.

Usage:
    gen_cdi.py <out.cdi> --version {v2,v3,v35} --spec <spec.json>
    gen_cdi.py --suite <out_dir>      # generate the standard fixture matrix
"""
import argparse, json, os, struct, sys

MAGIC = {"v2": 0x80000004, "v3": 0x80000005, "v35": 0x80000006}
MARK = bytes([0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF])
SECTOR_BYTES = {0: 2048, 1: 2336, 2: 2352}


def build_track_data(track, index):
    """Deterministic, verifiable payload: each sector filled with a byte
    pattern keyed to the track index so extraction tests can assert content."""
    size = SECTOR_BYTES[track["sector_size_code"]]
    total = track["pregap"] + track["length"]
    fill = (0x30 + index) & 0xFF          # '0','1','2',... per track
    return bytes([fill]) * (size * total)


def encode_track(track, index, running_offset):
    """Encode one canonical track block. Returns (bytes, stored_byte_len)."""
    fn = track.get("filename", f"TRACK{index:02d}.DAT").encode("ascii")
    if len(fn) > 255:
        raise ValueError("filename too long")
    b = bytearray()
    b += struct.pack("<I", 0)             # lead-in (0 in canonical; 0x80000000 reserved)
    b += MARK                            # start mark #1
    b += MARK                            # start mark #2
    b += struct.pack("<I", 0)            # reserved0
    b += struct.pack("<B", len(fn))      # filename length
    b += fn                              # filename
    b += struct.pack("<I", track["pregap"])
    b += struct.pack("<I", track["length"])
    b += struct.pack("<I", track["mode"])
    b += struct.pack("<I", track["start_lba"])
    total = track["pregap"] + track["length"]
    b += struct.pack("<I", total)
    b += struct.pack("<I", track["sector_size_code"])
    b += struct.pack("<I", 0)            # reserved1 (future ISRC/flags)
    return bytes(b), SECTOR_BYTES[track["sector_size_code"]] * total


def build(spec, version):
    """spec = {"sessions":[[track,...],...]}. Returns full CDI file bytes."""
    track_data = bytearray()
    descriptor = bytearray()

    sessions = spec["sessions"]
    descriptor += struct.pack("<H", len(sessions))

    index = 0
    for sess in sessions:
        descriptor += struct.pack("<H", len(sess))
        for track in sess:
            blk, _ = encode_track(track, index, len(track_data))
            descriptor += blk
            track_data += build_track_data(track, index)
            index += 1
        descriptor += struct.pack("<I", 0)   # session tail (canonical: 0)

    magic = MAGIC[version]
    desc_len = len(descriptor) + 8           # descriptor + trailer, for v35 locator
    if version == "v35":
        locator = desc_len                   # length-from-EOF
    else:
        locator = len(track_data)            # absolute offset of descriptor start
    trailer = struct.pack("<II", magic, locator)

    return bytes(track_data) + bytes(descriptor) + trailer


def default_suite():
    """The standard fixture matrix."""
    A = lambda: {"filename": "AUDIO01.WAV", "pregap": 150, "length": 1200,
                 "mode": 0, "start_lba": 0, "sector_size_code": 2}      # audio 2352
    D1 = lambda lba, n: {"filename": "DATA_M1.ISO", "pregap": 150, "length": n,
                         "mode": 1, "start_lba": lba, "sector_size_code": 0}  # mode1 2048
    D2 = lambda lba, n: {"filename": "DATA_M2.RAW", "pregap": 150, "length": n,
                         "mode": 2, "start_lba": lba, "sector_size_code": 1}  # mode2 2336
    return {
        "single_data_v2":       ("v2",  {"sessions": [[D1(0, 100)]]}),
        "single_data_v3":       ("v3",  {"sessions": [[D1(0, 100)]]}),
        "single_data_v35":      ("v35", {"sessions": [[D1(0, 100)]]}),
        "audio_data_v35":       ("v35", {"sessions": [[A()], [D1(45000, 200)]]}),
        "multitrack_mixed_v3":  ("v3",  {"sessions": [[A(), A(), D2(15000, 80)]]}),
        "three_session_v35":    ("v35", {"sessions": [[D1(0, 50)],
                                                       [D2(20000, 60)],
                                                       [D1(40000, 70)]]}),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out", nargs="?")
    ap.add_argument("--version", choices=MAGIC.keys())
    ap.add_argument("--spec")
    ap.add_argument("--suite")
    args = ap.parse_args()

    if args.suite:
        os.makedirs(args.suite, exist_ok=True)
        manifest = {}
        for name, (ver, spec) in default_suite().items():
            path = os.path.join(args.suite, f"{name}.cdi")
            with open(path, "wb") as f:
                f.write(build(spec, ver))
            manifest[name] = {"version": ver, "spec": spec,
                              "bytes": os.path.getsize(path)}
            print(f"wrote {path} ({manifest[name]['bytes']} bytes, {ver})")
        with open(os.path.join(args.suite, "manifest.json"), "w") as f:
            json.dump(manifest, f, indent=2)
        return

    if not (args.out and args.version and args.spec):
        ap.error("need <out> --version --spec, or --suite <dir>")
    spec = json.load(open(args.spec))
    with open(args.out, "wb") as f:
        f.write(build(spec, args.version))
    print(f"wrote {args.out}")


if __name__ == "__main__":
    main()
