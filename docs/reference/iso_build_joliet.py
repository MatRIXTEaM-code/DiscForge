#!/usr/bin/env python3
"""
Joliet + ISO 9660 dual-hierarchy builder (proves the algorithm).

An image with Joliet carries TWO directory hierarchies over ONE set of file
extents: the ISO 9660 tree (8.3 uppercase names, described by the PVD) and the
Joliet tree (UCS-2 / UTF-16BE long names, described by a type-2 Supplementary
Volume Descriptor). File data is stored once; both hierarchies point at it.

Validated with `isoinfo -J -f` (Joliet listing) + extraction, and `isoinfo -f`
(the plain ISO view still works). Ported to C# once green here.
"""
import struct, sys, os, subprocess, tempfile

SECTOR = 2048

def both_u32(v): return struct.pack("<I", v) + struct.pack(">I", v)
def both_u16(v): return struct.pack("<H", v) + struct.pack(">H", v)
def ceil_sec(n): return (n + SECTOR - 1) // SECTOR

def clean83(s):
    return "".join(c if (c.isalnum() and c.isascii()) else "_" for c in s.upper())

def iso_file_id(name):
    base, ext = os.path.splitext(os.path.basename(name))
    base = clean83(base)[:8] or "FILE"
    ext = clean83(ext.lstrip("."))[:3]
    return f"{base}.{ext};1" if ext else f"{base}.;1"

def iso_dir_id(name):
    return clean83(name)[:8] or "DIR"

def joliet_name(name):
    bad = set('*/:;?\\')
    n = "".join("_" if c in bad else c for c in os.path.basename(name))
    return n[:64]  # Joliet allows up to 64 UCS-2 chars

def ucs2(s): return s.encode("utf-16-be")

def dt7(): return bytes([125, 1, 1, 0, 0, 0, 0])

def write_dir_record(extent, data_len, is_dir, ident_bytes):
    reclen = 33 + len(ident_bytes)
    if reclen & 1: reclen += 1
    r = bytearray(reclen)
    r[0] = reclen
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
        self.name, self.is_dir, self.data = name, is_dir, data
        self.children = []
        self.iso_id = "\0"; self.jol = b"\x00"
        self.extent = 0; self.size = 0                    # file extent (shared)
        # per-hierarchy dir attributes:
        self.iso_extent = 0; self.iso_size = 0; self.iso_number = 0
        self.jol_extent = 0; self.jol_size = 0; self.jol_number = 0
        self.level = 0; self.parent = None

def dir_record_len(idlen):
    n = 33 + idlen
    return n + 1 if n & 1 else n

def content_size(d, id_of):
    pos = 34 + 34
    for c in sorted(d.children, key=lambda x: id_of(x)):
        rl = dir_record_len(len(id_of(c)))
        if pos % SECTOR + rl > SECTOR: pos += SECTOR - (pos % SECTOR)
        pos += rl
    return pos

def iso_ident(n):  return b"\x00" if n.name == "" else (n.iso_id.encode("ascii"))
def jol_ident(n):  return b"\x00" if n.name == "" else n.jol

def build(volume_id, root_children, joliet=True):
    root = Node("", True); root.children = root_children
    dirs = []
    def walk(node, level, parent):
        node.level, node.parent = level, parent
        if node.is_dir:
            dirs.append(node)
            for c in node.children:
                if c.is_dir: c.iso_id = iso_dir_id(c.name)
                else: c.iso_id = iso_file_id(c.name)
                c.jol = ucs2(joliet_name(c.name))
                walk(c, level+1, node)
    walk(root, 0, None)
    root.parent = root

    def number(dirs, id_of, set_num):
        order = [root]; set_num(root, 1); counter = 2
        maxl = max((d.level for d in dirs), default=0)
        for lvl in range(1, maxl+1):
            ds = [d for d in dirs if d.level == lvl]
            ds.sort(key=lambda d: (d.parent_number, id_of(d)))
            for d in ds: set_num(d, counter); counter += 1; order.append(d)
        return order

    # ISO numbering
    for d in dirs: d.parent_number = 0
    def set_iso(d, n): d.iso_number = n; d.parent_number = n
    # temporarily use parent_number for sort; assign per hierarchy
    def number_iso():
        order=[root]; root.iso_number=1; counter=2
        maxl=max((d.level for d in dirs),default=0)
        for lvl in range(1,maxl+1):
            ds=[d for d in dirs if d.level==lvl]
            ds.sort(key=lambda d:(d.parent.iso_number, iso_ident(d)))
            for d in ds: d.iso_number=counter; counter+=1; order.append(d)
        return order
    def number_jol():
        order=[root]; root.jol_number=1; counter=2
        maxl=max((d.level for d in dirs),default=0)
        for lvl in range(1,maxl+1):
            ds=[d for d in dirs if d.level==lvl]
            ds.sort(key=lambda d:(d.parent.jol_number, jol_ident(d)))
            for d in ds: d.jol_number=counter; counter+=1; order.append(d)
        return order
    iso_order = number_iso()
    jol_order = number_jol() if joliet else []

    def pt_size(order, id_of):
        tot = 0
        for d in order:
            idlen = 1 if d.name == "" else len(id_of(d))
            n = 8 + idlen
            tot += n + 1 if n & 1 else n
        return tot

    iso_pt = pt_size(iso_order, lambda d: d.iso_id.encode("ascii"))
    iso_pts = ceil_sec(iso_pt)
    jol_pt = pt_size(jol_order, lambda d: d.jol) if joliet else 0
    jol_pts = ceil_sec(jol_pt) if joliet else 0

    # layout
    n_vds = 3 if joliet else 2  # PVD, [SVD], terminator
    iso_ptL = 16 + n_vds
    iso_ptM = iso_ptL + iso_pts
    jol_ptL = iso_ptM + iso_pts
    jol_ptM = jol_ptL + jol_pts
    cursor = jol_ptM + jol_pts

    for d in iso_order:
        d.iso_size = content_size(d, lambda x: iso_ident(x))
        d.iso_extent = cursor; cursor += ceil_sec(d.iso_size)
    if joliet:
        for d in jol_order:
            d.jol_size = content_size(d, lambda x: jol_ident(x))
            d.jol_extent = cursor; cursor += ceil_sec(d.jol_size)

    files = []
    def collect(n):
        for c in n.children:
            if c.is_dir: collect(c)
            else: files.append(c)
    collect(root)
    for f in files:
        if len(f.data) == 0: f.extent = 0; f.size = 0
        else: f.extent = cursor; f.size = len(f.data); cursor += ceil_sec(len(f.data))

    volume_sectors = cursor
    img = bytearray(volume_sectors * SECTOR)

    def write_vd(sec, vtype, is_joliet):
        vd = bytearray(SECTOR)
        vd[0] = vtype; vd[1:6] = b"CD001"; vd[6] = 1
        if is_joliet:
            vd[88:91] = b"%/E"  # UCS-2 level 3 escape
            vid = ucs2(volume_id)[:32]; vd[40:40+len(vid)] = vid
            for i in range(40+len(vid), 72, 2): vd[i:i+2] = b"\x00\x20"
        else:
            vd[8:40] = b" "*32
            vid = volume_id.upper().encode("ascii")[:32]; vd[40:40+len(vid)] = vid
            vd[40+len(vid):72] = b" "*(32-len(vid))
        vd[80:88] = both_u32(volume_sectors)
        vd[120:124] = both_u16(1); vd[124:128] = both_u16(1); vd[128:132] = both_u16(SECTOR)
        if is_joliet:
            vd[132:140] = both_u32(jol_pt)
            vd[140:144] = struct.pack("<I", jol_ptL); vd[148:152] = struct.pack(">I", jol_ptM)
            vd[156:190] = write_dir_record(root.jol_extent, root.jol_size, True, b"\x00")
        else:
            vd[132:140] = both_u32(iso_pt)
            vd[140:144] = struct.pack("<I", iso_ptL); vd[148:152] = struct.pack(">I", iso_ptM)
            vd[156:190] = write_dir_record(root.iso_extent, root.iso_size, True, b"\x00")
        vd[813:830] = b"0"*16 + b"\x00"; vd[830:847] = b"0"*16 + b"\x00"
        vd[881] = 1
        img[sec*SECTOR:(sec+1)*SECTOR] = vd

    write_vd(16, 1, False)             # PVD
    if joliet: write_vd(17, 2, True)   # SVD (Joliet)
    term_sec = 18 if joliet else 17
    term = bytearray(SECTOR); term[0] = 0xFF; term[1:6] = b"CD001"; term[6] = 1
    img[term_sec*SECTOR:(term_sec+1)*SECTOR] = term

    def write_pt(base, sectors, order, id_of, num_of, little):
        buf = bytearray(sectors*SECTOR); p = 0
        for d in order:
            ident = b"\x00" if d.name == "" else id_of(d)
            buf[p] = len(ident)
            if little:
                buf[p+2:p+6] = struct.pack("<I", num_of(d)[0])
                buf[p+6:p+8] = struct.pack("<H", num_of(d)[1])
            else:
                buf[p+2:p+6] = struct.pack(">I", num_of(d)[0])
                buf[p+6:p+8] = struct.pack(">H", num_of(d)[1])
            buf[p+8:p+8+len(ident)] = ident
            rl = 8 + len(ident)
            if rl & 1: rl += 1
            p += rl
        img[base*SECTOR:base*SECTOR+len(buf)] = buf

    write_pt(iso_ptL, iso_pts, iso_order, lambda d: d.iso_id.encode("ascii"),
             lambda d: (d.iso_extent, d.parent.iso_number), True)
    write_pt(iso_ptM, iso_pts, iso_order, lambda d: d.iso_id.encode("ascii"),
             lambda d: (d.iso_extent, d.parent.iso_number), False)
    if joliet:
        write_pt(jol_ptL, jol_pts, jol_order, lambda d: d.jol,
                 lambda d: (d.jol_extent, d.parent.jol_number), True)
        write_pt(jol_ptM, jol_pts, jol_order, lambda d: d.jol,
                 lambda d: (d.jol_extent, d.parent.jol_number), False)

    def write_dirs(order, id_of, self_ext, self_size):
        for d in order:
            buf = bytearray(ceil_sec(self_size(d))*SECTOR); p = 0
            r = write_dir_record(self_ext(d), self_size(d), True, b"\x00"); buf[p:p+len(r)] = r; p += len(r)
            r = write_dir_record(self_ext(d.parent), self_size(d.parent), True, b"\x01"); buf[p:p+len(r)] = r; p += len(r)
            for c in sorted(d.children, key=lambda x: id_of(x)):
                ident = id_of(c)
                rl = dir_record_len(len(ident))
                if p % SECTOR + rl > SECTOR: p += SECTOR - (p % SECTOR)
                ext = c.extent if not c.is_dir else self_ext(c)
                sz = c.size if not c.is_dir else self_size(c)
                rec = write_dir_record(ext, sz, c.is_dir, ident)
                buf[p:p+len(rec)] = rec; p += len(rec)
            img[self_ext(d)*SECTOR:self_ext(d)*SECTOR+len(buf)] = buf

    write_dirs(iso_order, lambda x: iso_ident(x), lambda x: x.iso_extent, lambda x: x.iso_size)
    if joliet:
        write_dirs(jol_order, lambda x: jol_ident(x), lambda x: x.jol_extent, lambda x: x.jol_size)

    for f in files:
        if f.data: img[f.extent*SECTOR:f.extent*SECTOR+len(f.data)] = f.data
    return bytes(img)

if __name__ == "__main__":
    def F(n, d): return Node(n, False, d)
    def D(n, ch): x = Node(n, True); x.children = ch; return x
    tree = [
        F("My Readme.txt", b"hello long names\n"*8),
        D("Game Saves", [F("Slot 01 - Hero.sav", b"SAVE"*40)]),
        D("Docs", [F("manual.pdf", b"PDF"*100)]),
    ]
    img = build("DiscForge Disc", tree, joliet=True)
    with tempfile.NamedTemporaryFile(suffix=".iso", delete=False) as tf:
        tf.write(img); path = tf.name
    print(f"built {len(img)} bytes / {len(img)//SECTOR} sectors\n")
    print("=== isoinfo -J -f (Joliet long names) ===")
    print(subprocess.run(["isoinfo","-J","-f","-i",path], capture_output=True, text=True).stdout)
    print("=== isoinfo -f (plain ISO 8.3 still works) ===")
    print(subprocess.run(["isoinfo","-f","-i",path], capture_output=True, text=True).stdout)
    checks = {
        "/My Readme.txt": b"hello long names\n"*8,
        "/Game Saves/Slot 01 - Hero.sav": b"SAVE"*40,
        "/Docs/manual.pdf": b"PDF"*100,
    }
    allok = True
    for p, exp in checks.items():
        got = subprocess.run(["isoinfo","-J","-i",path,"-x",p], capture_output=True).stdout
        ok = got == exp; allok &= ok
        print(f"  {p:34} {'MATCH' if ok else 'DIFF'}")
    print("\nJOLIET VALIDATION:", "PASS" if allok else "FAIL")
    os.unlink(path)
