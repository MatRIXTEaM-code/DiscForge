#!/usr/bin/env python3
"""
Python mirror of DiscForge.Core.Iso.IsoBuilder (ISO 9660 Level 1).

Independent reimplementation of the same layout the C# builder produces, used to
validate the format against the third-party `isoinfo` tool: build here, then
`isoinfo -l -i out.iso` must list the files and `isoinfo -x /FILE.EXT;1` must
extract them byte-identical to the source. If isoinfo (a standards-compliant
reader) accepts our image, the layout is correct.

Not shipped — this is the executable oracle for the C# builder.
"""
import struct, sys, os

SECTOR = 2048

def both_u32(v): return struct.pack("<I", v) + struct.pack(">I", v)
def both_u16(v): return struct.pack("<H", v) + struct.pack(">H", v)
def ceil_sectors(n): return (n + SECTOR - 1) // SECTOR

def a_field(s, n):
    b = s.encode("ascii")[:n]
    return b + b" " * (n - len(b))

def dir_datetime():
    return bytes([125, 1, 1, 0, 0, 0, 0])  # fixed for determinism in the oracle

def dir_record_length(identifier):
    n = 33 + len(identifier)
    return n + 1 if n & 1 else n

def write_dir_record(extent, data_len, is_dir, identifier):
    if identifier == "\0": fi = b"\x00"
    elif identifier == "\x01": fi = b"\x01"
    else: fi = identifier.encode("ascii")
    reclen = 33 + len(fi)
    if reclen & 1: reclen += 1
    r = bytearray(reclen)
    r[0] = reclen
    r[1] = 0
    r[2:10] = both_u32(extent)
    r[10:18] = both_u32(data_len)
    r[18:25] = dir_datetime()
    r[25] = 0x02 if is_dir else 0x00
    r[28:32] = both_u16(1)
    r[32] = len(fi)
    r[33:33+len(fi)] = fi
    return bytes(r)

def to_level1(name):
    name = os.path.basename(name).upper()
    base, ext = os.path.splitext(name)
    ext = ext.lstrip(".")
    clean = lambda s: "".join(c if (c.isalnum() and c.isascii()) else "_" for c in s)
    base = clean(base) or "FILE"
    ext = clean(ext)
    base = base[:8]; ext = ext[:3]
    return f"{base}.{ext};1" if ext else f"{base}.;1"

def build(volume_id, files):
    entries = []
    seen = set()
    for name, data in files:
        i = to_level1(name)
        if i in seen: continue
        seen.add(i); entries.append((i, data))
    entries.sort(key=lambda e: e[0])

    pvd_sec, term_sec, ptl_sec, ptm_sec, root_sec = 16, 17, 18, 19, 20

    # root dir size
    pos = 34 + 34
    for i, _ in entries:
        rl = dir_record_length(i)
        if pos % SECTOR + rl > SECTOR:
            pos += SECTOR - (pos % SECTOR)
        pos += rl
    root_bytes = pos
    root_sectors = ceil_sectors(root_bytes)
    first_file = root_sec + root_sectors

    extents = []
    cur = first_file
    for i, data in entries:
        secs = 0 if len(data) == 0 else max(1, ceil_sectors(len(data)))
        extents.append((i, data, cur, secs))
        cur += secs
    volume_sectors = cur

    img = bytearray(volume_sectors * SECTOR)

    # PVD
    pvd = bytearray(SECTOR)
    pvd[0] = 1
    pvd[1:6] = b"CD001"
    pvd[6] = 1
    pvd[8:40] = a_field("", 32)
    pvd[40:72] = a_field(volume_id.upper(), 32)
    pvd[80:88] = both_u32(volume_sectors)
    pvd[120:124] = both_u16(1)
    pvd[124:128] = both_u16(1)
    pvd[128:132] = both_u16(SECTOR)
    pvd[132:140] = both_u32(10)
    pvd[140:144] = struct.pack("<I", ptl_sec)
    pvd[148:152] = struct.pack(">I", ptm_sec)
    pvd[156:190] = write_dir_record(root_sec, root_bytes, True, "\0")
    pvd[813:830] = b"0"*16 + b"\x00"
    pvd[830:847] = b"0"*16 + b"\x00"
    pvd[881] = 1
    img[pvd_sec*SECTOR:(pvd_sec+1)*SECTOR] = pvd

    # terminator
    term = bytearray(SECTOR); term[0] = 0xFF; term[1:6] = b"CD001"; term[6] = 1
    img[term_sec*SECTOR:(term_sec+1)*SECTOR] = term

    # path tables
    ptl = bytearray(SECTOR); ptl[0]=1; ptl[2:6]=struct.pack("<I",root_sec); ptl[6:8]=struct.pack("<H",1)
    img[ptl_sec*SECTOR:(ptl_sec+1)*SECTOR] = ptl
    ptm = bytearray(SECTOR); ptm[0]=1; ptm[2:6]=struct.pack(">I",root_sec); ptm[6:8]=struct.pack(">H",1)
    img[ptm_sec*SECTOR:(ptm_sec+1)*SECTOR] = ptm

    # root dir
    root = bytearray(root_sectors*SECTOR)
    p = 0
    r = write_dir_record(root_sec, root_bytes, True, "\0"); root[p:p+len(r)] = r; p += len(r)
    r = write_dir_record(root_sec, root_bytes, True, "\x01"); root[p:p+len(r)] = r; p += len(r)
    for i, data, extent, _ in extents:
        rl = dir_record_length(i)
        if p % SECTOR + rl > SECTOR:
            p += SECTOR - (p % SECTOR)
        r = write_dir_record(extent, len(data), False, i)
        root[p:p+len(r)] = r; p += len(r)
    img[root_sec*SECTOR:root_sec*SECTOR+len(root)] = root

    # files
    for i, data, extent, _ in extents:
        if data: img[extent*SECTOR:extent*SECTOR+len(data)] = data

    return bytes(img)

if __name__ == "__main__":
    out = sys.argv[1]
    files = [
        ("README.TXT", b"DiscForge ISO builder validation.\n"*20),
        ("MARKER.DAT", b"0123456789ABCDEF"*100),
        ("data.bin",   bytes(range(256))*10),
    ]
    img = build("OJISO", files)
    open(out, "wb").write(img)
    print(f"wrote {out} ({len(img)} bytes, {len(img)//SECTOR} sectors)")
