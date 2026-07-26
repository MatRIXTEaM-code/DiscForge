#!/usr/bin/env python3
"""
Rock Ridge (SUSP/RRIP) builder — proves the System Use entries.

Rock Ridge adds POSIX semantics to the ISO 9660 primary tree by appending
System Use entries to each directory record:
  SP (once, in root '.'), ER (Rock Ridge extension id), PX (mode/nlink/uid/gid),
  TF (timestamps), NM (the real long name). Names bypass the 8.3 limit.

Validated with `isoinfo -R -l` (Rock Ridge long names + Unix perms). The RR bits
are then ported into the C# IsoBuilder's ISO hierarchy (Joliet/El Torito stay).
"""
import struct, os, subprocess, tempfile, sys

SECTOR = 2048
def both_u32(v): return struct.pack("<I", v) + struct.pack(">I", v)
def both_u16(v): return struct.pack("<H", v) + struct.pack(">H", v)
def ceil_sec(n): return (n + SECTOR - 1) // SECTOR
def clean83(s): return "".join(c if (c.isalnum() and c.isascii()) else "_" for c in s.upper())
def iso_file_id(name):
    b, e = os.path.splitext(os.path.basename(name)); b = clean83(b)[:8] or "FILE"; e = clean83(e.lstrip("."))[:3]
    return f"{b}.{e};1" if e else f"{b}.;1"
def iso_dir_id(name): return clean83(name)[:8] or "DIR"
def dt7(): return bytes([125, 1, 1, 0, 0, 0, 0])

# ---- SUSP / RRIP entries ----
def SP(): return b"SP" + bytes([7, 1, 0xBE, 0xEF, 0])
def ER():
    idn = b"RRIP_1991A"
    return b"ER" + bytes([8 + len(idn), 1, len(idn), 0, 0, 1]) + idn
def PX(is_dir):
    mode = 0o040755 if is_dir else 0o100644
    nlink = 2 if is_dir else 1
    return b"PX" + bytes([36, 1]) + both_u32(mode) + both_u32(nlink) + both_u32(0) + both_u32(0)
def TF():
    return b"TF" + bytes([12, 1, 0x02]) + dt7()   # modify time, short form
def NM(name):
    nb = name.encode("utf-8")
    return b"NM" + bytes([5 + len(nb), 1, 0]) + nb

def su_for(kind, name, is_dir, identlen=1):
    """Build the System Use area. `identlen` is the ISO identifier byte length of
    the record this SU belongs to — needed to cap NM so the whole directory
    record stays <= 255 bytes (reclen is a single byte and would otherwise
    overflow for very long names)."""
    out = b""
    if kind == "root_self":
        out += SP() + ER()
    if kind in ("file", "dir"):
        base_even = 33 + identlen
        if base_even & 1: base_even += 1
        max_nm = 254 - base_even - 5 - len(PX(is_dir)) - len(TF())
        nb = name.encode("utf-8")
        if len(nb) > max_nm and max_nm > 0:
            name = nb[:max_nm].decode("utf-8", "ignore")
        out += NM(name)
    out += PX(is_dir) + TF()
    return out

class Node:
    def __init__(self, name, is_dir, data=None):
        self.name, self.is_dir, self.data = name, is_dir, data
        self.children = []; self.iso_id = "\0"
        self.extent = self.size = 0
        self.iso_extent = self.iso_size = self.iso_number = 0
        self.level = 0; self.parent = None

def iso_ident(n): return b"\x00" if n.name == "" else n.iso_id.encode("ascii")

def dir_record_len_rr(idlen, su_len):
    base = 33 + idlen
    if base & 1: base += 1
    reclen = base + su_len
    if reclen & 1: reclen += 1
    return reclen

def write_dir_record_rr(extent, data_len, is_dir, ident, su):
    base = 33 + len(ident)
    if base & 1: base += 1
    reclen = base + len(su)
    if reclen & 1: reclen += 1
    r = bytearray(reclen); r[0] = reclen
    r[2:10] = both_u32(extent); r[10:18] = both_u32(data_len); r[18:25] = dt7()
    r[25] = 0x02 if is_dir else 0x00; r[28:32] = both_u16(1)
    r[32] = len(ident); r[33:33+len(ident)] = ident
    r[base:base+len(su)] = su
    return bytes(r)

def content_size(d, is_root):
    pos = 0
    # '.' (root self carries SP+ER)
    pos += dir_record_len_rr(1, len(su_for("root_self" if is_root else "self", ".", True)))
    pos += dir_record_len_rr(1, len(su_for("parent", "..", True)))
    for c in sorted(d.children, key=lambda x: iso_ident(x)):
        su = su_for("dir" if c.is_dir else "file", c.name, c.is_dir, len(iso_ident(c)))
        rl = dir_record_len_rr(len(iso_ident(c)), len(su))
        if pos % SECTOR + rl > SECTOR: pos += SECTOR - (pos % SECTOR)
        pos += rl
    return pos

def build(volume_id, root_children):
    root = Node("", True); root.children = root_children
    dirs = []
    def walk(node, level, parent):
        node.level, node.parent = level, parent
        if node.is_dir:
            dirs.append(node)
            for c in node.children:
                c.iso_id = iso_dir_id(c.name) if c.is_dir else iso_file_id(c.name)
                walk(c, level+1, node)
    walk(root, 0, None); root.parent = root

    order = [root]; root.iso_number = 1; counter = 2
    maxl = max((d.level for d in dirs), default=0)
    for lvl in range(1, maxl+1):
        ds = [d for d in dirs if d.level == lvl]
        ds.sort(key=lambda d: (d.parent.iso_number, iso_ident(d)))
        for d in ds: d.iso_number = counter; counter += 1; order.append(d)

    def pt_size():
        tot = 0
        for d in order:
            idlen = 1 if d.name == "" else len(d.iso_id.encode("ascii"))
            n = 8 + idlen; tot += n + 1 if n & 1 else n
        return tot
    ptsz = pt_size(); ptsec = ceil_sec(ptsz)

    ptL = 18; ptM = ptL + ptsec; cursor = ptM + ptsec
    for d in order:
        d.iso_size = content_size(d, d is root); d.iso_extent = cursor; cursor += ceil_sec(d.iso_size)
    files = []
    def collect(n):
        for c in n.children:
            if c.is_dir: collect(c)
            else: files.append(c)
    collect(root)
    for f in files:
        if len(f.data) == 0: f.extent = 0
        else: f.extent = cursor; f.size = len(f.data); cursor += ceil_sec(len(f.data))
    vol_sec = cursor; img = bytearray(vol_sec*SECTOR)

    pvd = bytearray(SECTOR); pvd[0] = 1; pvd[1:6] = b"CD001"; pvd[6] = 1
    pvd[8:40] = b" "*32; v = volume_id.upper().encode()[:32]; pvd[40:40+len(v)] = v; pvd[40+len(v):72] = b" "*(32-len(v))
    pvd[80:88] = both_u32(vol_sec); pvd[120:124] = both_u16(1); pvd[124:128] = both_u16(1); pvd[128:132] = both_u16(SECTOR)
    pvd[132:140] = both_u32(ptsz); pvd[140:144] = struct.pack("<I", ptL); pvd[148:152] = struct.pack(">I", ptM)
    # PVD root record is plain 34 bytes (no SU here)
    rr = bytearray(34); rr[0] = 34; rr[2:10] = both_u32(root.iso_extent); rr[10:18] = both_u32(root.iso_size)
    rr[18:25] = dt7(); rr[25] = 0x02; rr[28:32] = both_u16(1); rr[32] = 1
    pvd[156:190] = rr
    pvd[813:830] = b"0"*16+b"\x00"; pvd[830:847] = b"0"*16+b"\x00"; pvd[881] = 1
    img[16*SECTOR:17*SECTOR] = pvd
    term = bytearray(SECTOR); term[0] = 0xFF; term[1:6] = b"CD001"; term[6] = 1
    img[17*SECTOR:18*SECTOR] = term

    def write_pt(base, little):
        buf = bytearray(ptsec*SECTOR); p = 0
        for d in order:
            ident = b"\x00" if d.name == "" else d.iso_id.encode("ascii")
            buf[p] = len(ident)
            if little: buf[p+2:p+6] = struct.pack("<I", d.iso_extent); buf[p+6:p+8] = struct.pack("<H", d.parent.iso_number)
            else: buf[p+2:p+6] = struct.pack(">I", d.iso_extent); buf[p+6:p+8] = struct.pack(">H", d.parent.iso_number)
            buf[p+8:p+8+len(ident)] = ident
            rl = 8+len(ident);
            if rl & 1: rl += 1
            p += rl
        img[base*SECTOR:base*SECTOR+len(buf)] = buf
    write_pt(ptL, True); write_pt(ptM, False)

    for d in order:
        buf = bytearray(ceil_sec(d.iso_size)*SECTOR); p = 0
        su_self = su_for("root_self" if d is root else "self", ".", True)
        r = write_dir_record_rr(d.iso_extent, d.iso_size, True, b"\x00", su_self); buf[p:p+len(r)] = r; p += len(r)
        r = write_dir_record_rr(d.parent.iso_extent, d.parent.iso_size, True, b"\x01", su_for("parent","..",True)); buf[p:p+len(r)] = r; p += len(r)
        for c in sorted(d.children, key=lambda x: iso_ident(x)):
            ident = iso_ident(c)
            su = su_for("dir" if c.is_dir else "file", c.name, c.is_dir, len(ident))
            rl = dir_record_len_rr(len(ident), len(su))
            if p % SECTOR + rl > SECTOR: p += SECTOR - (p % SECTOR)
            ext = c.extent if not c.is_dir else c.iso_extent
            sz = c.size if not c.is_dir else c.iso_size
            rec = write_dir_record_rr(ext, sz, c.is_dir, ident, su); buf[p:p+len(rec)] = rec; p += len(rec)
        img[d.iso_extent*SECTOR:d.iso_extent*SECTOR+len(buf)] = buf

    for f in files:
        if f.data: img[f.extent*SECTOR:f.extent*SECTOR+len(f.data)] = f.data
    return bytes(img)

if __name__ == "__main__":
    F = lambda n, d: Node(n, False, d)
    def D(n, ch): x = Node(n, True); x.children = ch; return x
    tree = [F("a-long-unix-name.tar.gz", b"data\n"*20),
            F("z"*200 + ".dat", b"capped\n"*5),
            D("source-code", [F("main.c", b"int main(){}\n"*10),
                              D("include", [F("header-file.h", b"#pragma once\n"*5)])])]
    img = build("RRDISC", tree)
    with tempfile.NamedTemporaryFile(suffix=".iso", delete=False) as tf: tf.write(img); path = tf.name
    print(f"built {len(img)} bytes / {len(img)//SECTOR} sectors\n")
    print("=== isoinfo -R -l (Rock Ridge long names + Unix perms) ===")
    print(subprocess.run(["isoinfo","-R","-l","-i",path], capture_output=True, text=True).stdout)
    print("=== isoinfo -f (plain ISO 8.3 fallback) ===")
    print(subprocess.run(["isoinfo","-f","-i",path], capture_output=True, text=True).stdout)
    checks = {"/a-long-unix-name.tar.gz": b"data\n"*20,
              "/source-code/main.c": b"int main(){}\n"*10,
              "/source-code/include/header-file.h": b"#pragma once\n"*5}
    allok = True
    for p, exp in checks.items():
        got = subprocess.run(["isoinfo","-R","-i",path,"-x",p], capture_output=True).stdout
        ok = got == exp; allok &= ok
        print(f"  {p:38} {'MATCH' if ok else 'DIFF'}")
    print("\nROCK RIDGE VALIDATION:", "PASS" if allok else "FAIL")
    os.unlink(path)
