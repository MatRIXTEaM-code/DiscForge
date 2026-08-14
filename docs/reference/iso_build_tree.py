#!/usr/bin/env python3
"""
Tree-capable ISO 9660 Level 1 builder (proves the subdirectory algorithm).

Extends the flat iso_build.py to a real directory tree: recursive directory
records, multi-entry Type-L/Type-M path tables with parent pointers, and
canonical path-table ordering (level, parent number, identifier). Validated with
`isoinfo -f` (full pathnames) + extraction. Once green here, the same layout is
ported to C# IsoBuilder.
"""
import struct, sys, os, subprocess, tempfile

SECTOR = 2048

def both_u32(v): return struct.pack("<I", v) + struct.pack(">I", v)
def both_u16(v): return struct.pack("<H", v) + struct.pack(">H", v)
def ceil_sec(n): return (n + SECTOR - 1) // SECTOR

def clean(s):
    return "".join(c if (c.isalnum() and c.isascii()) else "_" for c in s.upper())

def file_id(name):
    base, ext = os.path.splitext(os.path.basename(name))
    base = clean(base)[:8] or "FILE"
    ext = clean(ext.lstrip("."))[:3]
    return f"{base}.{ext};1" if ext else f"{base}.;1"

def dir_id(name):
    return (clean(name)[:8] or "DIR")

def dir_record_len(id_bytes_len):
    n = 33 + id_bytes_len
    return n + 1 if n & 1 else n

def dt7():
    return bytes([125, 1, 1, 0, 0, 0, 0])

def write_dir_record(extent, data_len, is_dir, ident_bytes):
    reclen = 33 + len(ident_bytes)
    if reclen & 1: reclen += 1
    r = bytearray(reclen)
    r[0] = reclen; r[1] = 0
    r[2:10] = both_u32(extent)
    r[10:18] = both_u32(data_len)
    r[18:25] = dt7()
    r[25] = 0x02 if is_dir else 0x00
    r[28:32] = both_u16(1)
    r[32] = len(ident_bytes)
    r[33:33+len(ident_bytes)] = ident_bytes
    return bytes(r)

class Node:
    def __init__(self, name, is_dir, data=None):
        self.name = name; self.is_dir = is_dir; self.data = data
        self.children = []
        self.id = "\0"  # set for non-root
        self.extent = 0; self.size = 0; self.number = 0; self.level = 0; self.parent = None

def dir_content_size(d):
    pos = 34 + 34
    for c in sorted(d.children, key=lambda x: x.id):
        idlen = 1 if False else len(c.id.encode("ascii"))
        rl = dir_record_len(idlen)
        if pos % SECTOR + rl > SECTOR:
            pos += SECTOR - (pos % SECTOR)
        pos += rl
    return pos

def build(volume_id, root_children):
    root = Node("", True)
    root.children = root_children

    # assign levels, parents, ids
    dirs = []
    def walk(node, level, parent):
        node.level = level; node.parent = parent
        if node.is_dir:
            dirs.append(node)
            for c in node.children:
                c.id = dir_id(c.name) if c.is_dir else file_id(c.name)
                walk(c, level + 1, node)
    walk(root, 0, None)
    root.parent = root  # root's parent is itself

    # number directories in path-table order: level, then parent number, then id
    order = [root]; root.number = 1; counter = 2
    max_level = max(d.level for d in dirs)
    for lvl in range(1, max_level + 1):
        level_dirs = [d for d in dirs if d.level == lvl]
        level_dirs.sort(key=lambda d: (d.parent.number, d.id))
        for d in level_dirs:
            d.number = counter; counter += 1; order.append(d)

    # path table size (bytes)
    def pt_rec_len(d):
        idlen = 1 if d is root else len(d.id.encode("ascii"))
        n = 8 + idlen
        return n + 1 if n & 1 else n
    pt_size = sum(pt_rec_len(d) for d in order)
    pt_sectors = ceil_sec(pt_size)

    # layout
    ptL = 18
    ptM = 18 + pt_sectors
    cursor = 18 + 2 * pt_sectors

    # directory extents in path-table order
    for d in order:
        d.size = dir_content_size(d)
        d.extent = cursor
        cursor += ceil_sec(d.size)

    # file extents (DFS order)
    files = []
    def collect(node):
        for c in node.children:
            if c.is_dir: collect(c)
            else: files.append(c)
    collect(root)
    for f in files:
        if len(f.data) == 0:
            f.extent = 0; f.size = 0
        else:
            f.extent = cursor; f.size = len(f.data)
            cursor += ceil_sec(len(f.data))

    volume_sectors = cursor
    img = bytearray(volume_sectors * SECTOR)

    # PVD
    pvd = bytearray(SECTOR)
    pvd[0] = 1; pvd[1:6] = b"CD001"; pvd[6] = 1
    pvd[8:40] = b" " * 32
    vid = volume_id.upper().encode("ascii")[:32]; pvd[40:40+len(vid)] = vid; pvd[40+len(vid):72] = b" " * (32 - len(vid))
    pvd[80:88] = both_u32(volume_sectors)
    pvd[120:124] = both_u16(1); pvd[124:128] = both_u16(1); pvd[128:132] = both_u16(SECTOR)
    pvd[132:140] = both_u32(pt_size)
    pvd[140:144] = struct.pack("<I", ptL)
    pvd[148:152] = struct.pack(">I", ptM)
    pvd[156:190] = write_dir_record(root.extent, root.size, True, b"\x00")
    pvd[813:830] = b"0"*16 + b"\x00"; pvd[830:847] = b"0"*16 + b"\x00"
    pvd[881] = 1
    img[16*SECTOR:17*SECTOR] = pvd

    # terminator
    term = bytearray(SECTOR); term[0] = 0xFF; term[1:6] = b"CD001"; term[6] = 1
    img[17*SECTOR:18*SECTOR] = term

    # path tables
    def write_pt(little):
        buf = bytearray(pt_sectors * SECTOR)
        p = 0
        for d in order:
            ident = b"\x00" if d is root else d.id.encode("ascii")
            buf[p] = len(ident); buf[p+1] = 0
            if little:
                buf[p+2:p+6] = struct.pack("<I", d.extent)
                buf[p+6:p+8] = struct.pack("<H", d.parent.number)
            else:
                buf[p+2:p+6] = struct.pack(">I", d.extent)
                buf[p+6:p+8] = struct.pack(">H", d.parent.number)
            buf[p+8:p+8+len(ident)] = ident
            rl = 8 + len(ident)
            if rl & 1: rl += 1
            p += rl
        return buf
    img[ptL*SECTOR:ptL*SECTOR+pt_sectors*SECTOR] = write_pt(True)
    img[ptM*SECTOR:ptM*SECTOR+pt_sectors*SECTOR] = write_pt(False)

    # directory records
    for d in order:
        buf = bytearray(ceil_sec(d.size) * SECTOR)
        p = 0
        p_ = write_dir_record(d.extent, d.size, True, b"\x00"); buf[p:p+len(p_)] = p_; p += len(p_)
        pp = d.parent
        p_ = write_dir_record(pp.extent, pp.size, True, b"\x01"); buf[p:p+len(p_)] = p_; p += len(p_)
        for c in sorted(d.children, key=lambda x: x.id):
            ident = c.id.encode("ascii")
            rl = dir_record_len(len(ident))
            if p % SECTOR + rl > SECTOR:
                p += SECTOR - (p % SECTOR)
            rec = write_dir_record(c.extent, c.size, c.is_dir, ident)
            buf[p:p+len(rec)] = rec; p += len(rec)
        img[d.extent*SECTOR:d.extent*SECTOR+len(buf)] = buf

    # file data
    for f in files:
        if f.data:
            img[f.extent*SECTOR:f.extent*SECTOR+len(f.data)] = f.data

    return bytes(img)

# ---- self-test with isoinfo ----
if __name__ == "__main__":
    def F(name, data): n = Node(name, False, data); return n
    def D(name, children): n = Node(name, True); n.children = children; return n

    tree = [
        F("readme.txt", b"root readme\n" * 10),
        D("games", [
            F("sonic.bin", bytes(range(256)) * 20),
            D("saves", [F("slot1.sav", b"SAVE" * 64)]),
        ]),
        D("docs", [F("manual.txt", b"the manual\n" * 30)]),
    ]
    img = build("OJTREE", tree)
    with tempfile.NamedTemporaryFile(suffix=".iso", delete=False) as tf:
        tf.write(img); path = tf.name
    print(f"built {len(img)} bytes / {len(img)//SECTOR} sectors\n")

    print("=== isoinfo -f (full pathnames) ===")
    print(subprocess.run(["isoinfo", "-f", "-i", path], capture_output=True, text=True).stdout)

    checks = {
        "/README.TXT;1": b"root readme\n" * 10,
        "/GAMES/SONIC.BIN;1": bytes(range(256)) * 20,
        "/GAMES/SAVES/SLOT1.SAV;1": b"SAVE" * 64,
        "/DOCS/MANUAL.TXT;1": b"the manual\n" * 30,
    }
    allok = True
    for p, expected in checks.items():
        got = subprocess.run(["isoinfo", "-i", path, "-x", p], capture_output=True).stdout
        ok = got == expected; allok &= ok
        print(f"  {p:28} {len(got):5}b  {'MATCH' if ok else 'DIFF'}")
    print("\nTREE ISO (nested dirs) VALIDATION:", "PASS" if allok else "FAIL")
    os.unlink(path)
