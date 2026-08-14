#!/usr/bin/env python3
"""
El Torito bootable-disc support (superset of the Joliet builder).

Adds, on top of tree + Joliet:
  - a Boot Record Volume Descriptor (type 0) naming "EL TORITO SPECIFICATION"
    and pointing at the boot catalog LBA;
  - a Boot Catalog: validation entry (checksummed, 0x55AA) + default entry
    (bootable, media type, load RBA -> boot image);
  - the boot image data itself, stored at its own extent.

Validated with `isoinfo -d` (reports the El Torito VD + catalog sector) plus a
manual catalog-structure/checksum check. The boot image is caller-supplied, so
nothing copyrighted is embedded. Ported to C# once green.
"""
import struct, sys, os, subprocess, tempfile

SECTOR = 2048
def both_u32(v): return struct.pack("<I", v) + struct.pack(">I", v)
def both_u16(v): return struct.pack("<H", v) + struct.pack(">H", v)
def ceil_sec(n): return (n + SECTOR - 1) // SECTOR
def clean83(s): return "".join(c if (c.isalnum() and c.isascii()) else "_" for c in s.upper())
def iso_file_id(name):
    base, ext = os.path.splitext(os.path.basename(name))
    base = clean83(base)[:8] or "FILE"; ext = clean83(ext.lstrip("."))[:3]
    return f"{base}.{ext};1" if ext else f"{base}.;1"
def iso_dir_id(name): return clean83(name)[:8] or "DIR"
def joliet_name(name):
    bad = set('*/:;?\\'); return "".join("_" if c in bad else c for c in os.path.basename(name))[:64]
def ucs2(s): return s.encode("utf-16-be")
def dt7(): return bytes([125, 1, 1, 0, 0, 0, 0])

def write_dir_record(extent, data_len, is_dir, ident):
    reclen = 33 + len(ident)
    if reclen & 1: reclen += 1
    r = bytearray(reclen); r[0] = reclen
    r[2:10] = both_u32(extent); r[10:18] = both_u32(data_len); r[18:25] = dt7()
    r[25] = 0x02 if is_dir else 0x00; r[28:32] = both_u16(1)
    r[32] = len(ident); r[33:33+len(ident)] = ident
    return bytes(r)

class Node:
    def __init__(self, name, is_dir, data=None):
        self.name, self.is_dir, self.data = name, is_dir, data
        self.children = []; self.iso_id = "\0"; self.jol = b"\x00"
        self.extent = 0; self.size = 0
        self.iso_extent=self.iso_size=self.iso_number=0
        self.jol_extent=self.jol_size=self.jol_number=0
        self.level=0; self.parent=None

def dir_record_len(idlen):
    n = 33 + idlen; return n + 1 if n & 1 else n
def content_size(d, id_of):
    pos = 68
    for c in sorted(d.children, key=lambda x: id_of(x)):
        rl = dir_record_len(len(id_of(c)))
        if pos % SECTOR + rl > SECTOR: pos += SECTOR - (pos % SECTOR)
        pos += rl
    return pos
def iso_ident(n): return b"\x00" if n.name=="" else n.iso_id.encode("ascii")
def jol_ident(n): return b"\x00" if n.name=="" else n.jol

def build(volume_id, root_children, joliet=True, boot_image=None, boot_media=0):
    root = Node("", True); root.children = root_children
    dirs = []
    def walk(node, level, parent):
        node.level, node.parent = level, parent
        if node.is_dir:
            dirs.append(node)
            for c in node.children:
                c.iso_id = iso_dir_id(c.name) if c.is_dir else iso_file_id(c.name)
                c.jol = ucs2(joliet_name(c.name)); walk(c, level+1, node)
    walk(root, 0, None); root.parent = root

    def number(id_of, set_get):
        order=[root]; set_get(root,1); counter=2
        maxl=max((d.level for d in dirs),default=0)
        for lvl in range(1,maxl+1):
            ds=[d for d in dirs if d.level==lvl]
            ds.sort(key=lambda d:(set_get(d.parent,None), id_of(d)))
            for d in ds: set_get(d,counter); counter+=1; order.append(d)
        return order
    def iso_num(d,v):
        if v is None: return d.iso_number
        d.iso_number=v
    def jol_num(d,v):
        if v is None: return d.jol_number
        d.jol_number=v
    iso_order=number(lambda d: iso_ident(d), iso_num)
    jol_order=number(lambda d: jol_ident(d), jol_num) if joliet else []

    def pt_size(order, id_of, num_of):
        tot=0
        for d in order:
            idlen = 1 if d.name=="" else len(id_of(d))
            n=8+idlen; tot += n+1 if n&1 else n
        return tot
    iso_pt=pt_size(iso_order, lambda d:d.iso_id.encode("ascii"), None); iso_pts=ceil_sec(iso_pt)
    jol_pt=pt_size(jol_order, lambda d:d.jol, None) if joliet else 0; jol_pts=ceil_sec(jol_pt) if joliet else 0

    # volume descriptor sectors: PVD, [BootRecord], [SVD], terminator
    vd_sectors=[16]; pvd_sec=16
    nxt=17
    boot_rec_sec=None; svd_sec=None
    if boot_image is not None: boot_rec_sec=nxt; nxt+=1
    if joliet: svd_sec=nxt; nxt+=1
    term_sec=nxt; nxt+=1
    data_start=nxt

    iso_ptL=data_start; iso_ptM=iso_ptL+iso_pts
    jol_ptL=iso_ptM+iso_pts; jol_ptM=jol_ptL+jol_pts
    cursor=jol_ptM+jol_pts

    for d in iso_order:
        d.iso_size=content_size(d, iso_ident); d.iso_extent=cursor; cursor+=ceil_sec(d.iso_size)
    if joliet:
        for d in jol_order:
            d.jol_size=content_size(d, jol_ident); d.jol_extent=cursor; cursor+=ceil_sec(d.jol_size)

    boot_cat_sec=None; boot_img_sec=None
    if boot_image is not None:
        boot_cat_sec=cursor; cursor+=1
        boot_img_sec=cursor; cursor+=ceil_sec(len(boot_image))

    files=[]
    def collect(n):
        for c in n.children:
            if c.is_dir: collect(c)
            else: files.append(c)
    collect(root)
    for f in files:
        if len(f.data)==0: f.extent=0; f.size=0
        else: f.extent=cursor; f.size=len(f.data); cursor+=ceil_sec(len(f.data))

    volume_sectors=cursor
    img=bytearray(volume_sectors*SECTOR)

    def write_vd(sec, vtype, is_joliet):
        vd=bytearray(SECTOR); vd[0]=vtype; vd[1:6]=b"CD001"; vd[6]=1
        if is_joliet:
            vd[88:91]=b"%/E"; v=ucs2(volume_id)[:32]; vd[40:40+len(v)]=v
            for i in range(40+len(v),72,2): vd[i:i+2]=b"\x00\x20"
        else:
            vd[8:40]=b" "*32; v=volume_id.upper().encode("ascii")[:32]
            vd[40:40+len(v)]=v; vd[40+len(v):72]=b" "*(32-len(v))
        vd[80:88]=both_u32(volume_sectors)
        vd[120:124]=both_u16(1); vd[124:128]=both_u16(1); vd[128:132]=both_u16(SECTOR)
        if is_joliet:
            vd[132:140]=both_u32(jol_pt); vd[140:144]=struct.pack("<I",jol_ptL); vd[148:152]=struct.pack(">I",jol_ptM)
            vd[156:190]=write_dir_record(root.jol_extent, root.jol_size, True, b"\x00")
        else:
            vd[132:140]=both_u32(iso_pt); vd[140:144]=struct.pack("<I",iso_ptL); vd[148:152]=struct.pack(">I",iso_ptM)
            vd[156:190]=write_dir_record(root.iso_extent, root.iso_size, True, b"\x00")
        vd[813:830]=b"0"*16+b"\x00"; vd[830:847]=b"0"*16+b"\x00"; vd[881]=1
        img[sec*SECTOR:(sec+1)*SECTOR]=vd

    write_vd(pvd_sec,1,False)
    if boot_rec_sec is not None:
        br=bytearray(SECTOR); br[0]=0; br[1:6]=b"CD001"; br[6]=1
        bsi=b"EL TORITO SPECIFICATION"; br[7:7+len(bsi)]=bsi  # padded with zeros to 32
        br[71:75]=struct.pack("<I", boot_cat_sec)  # boot catalog LBA (LSB)
        img[boot_rec_sec*SECTOR:(boot_rec_sec+1)*SECTOR]=br
    if joliet: write_vd(svd_sec,2,True)
    term=bytearray(SECTOR); term[0]=0xFF; term[1:6]=b"CD001"; term[6]=1
    img[term_sec*SECTOR:(term_sec+1)*SECTOR]=term

    def write_pt(base,sectors,order,id_of,extent_of,parentnum_of,little):
        buf=bytearray(sectors*SECTOR); p=0
        for d in order:
            ident=b"\x00" if d.name=="" else id_of(d)
            buf[p]=len(ident)
            if little:
                buf[p+2:p+6]=struct.pack("<I",extent_of(d)); buf[p+6:p+8]=struct.pack("<H",parentnum_of(d))
            else:
                buf[p+2:p+6]=struct.pack(">I",extent_of(d)); buf[p+6:p+8]=struct.pack(">H",parentnum_of(d))
            buf[p+8:p+8+len(ident)]=ident
            rl=8+len(ident);
            if rl&1: rl+=1
            p+=rl
        img[base*SECTOR:base*SECTOR+len(buf)]=buf
    write_pt(iso_ptL,iso_pts,iso_order,lambda d:d.iso_id.encode("ascii"),lambda d:d.iso_extent,lambda d:d.parent.iso_number,True)
    write_pt(iso_ptM,iso_pts,iso_order,lambda d:d.iso_id.encode("ascii"),lambda d:d.iso_extent,lambda d:d.parent.iso_number,False)
    if joliet:
        write_pt(jol_ptL,jol_pts,jol_order,lambda d:d.jol,lambda d:d.jol_extent,lambda d:d.parent.jol_number,True)
        write_pt(jol_ptM,jol_pts,jol_order,lambda d:d.jol,lambda d:d.jol_extent,lambda d:d.parent.jol_number,False)

    def write_dirs(order,id_of,ext_of,size_of):
        for d in order:
            buf=bytearray(ceil_sec(size_of(d))*SECTOR); p=0
            r=write_dir_record(ext_of(d),size_of(d),True,b"\x00"); buf[p:p+len(r)]=r; p+=len(r)
            r=write_dir_record(ext_of(d.parent),size_of(d.parent),True,b"\x01"); buf[p:p+len(r)]=r; p+=len(r)
            for c in sorted(d.children,key=lambda x:id_of(x)):
                ident=id_of(c); rl=dir_record_len(len(ident))
                if p%SECTOR+rl>SECTOR: p+=SECTOR-(p%SECTOR)
                ext=c.extent if not c.is_dir else ext_of(c)
                sz=c.size if not c.is_dir else size_of(c)
                rec=write_dir_record(ext,sz,c.is_dir,ident); buf[p:p+len(rec)]=rec; p+=len(rec)
            img[ext_of(d)*SECTOR:ext_of(d)*SECTOR+len(buf)]=buf
    write_dirs(iso_order,iso_ident,lambda x:x.iso_extent,lambda x:x.iso_size)
    if joliet: write_dirs(jol_order,jol_ident,lambda x:x.jol_extent,lambda x:x.jol_size)

    # Boot catalog + boot image
    if boot_image is not None:
        cat=bytearray(SECTOR)
        # Validation entry
        cat[0]=1        # header id
        cat[1]=0        # platform 80x86
        # id string bytes 4..27 zero
        cat[30]=0x55; cat[31]=0xAA
        # checksum: sum of all 16 words == 0
        s=sum(struct.unpack_from("<H",cat,i)[0] for i in range(0,32,2)) & 0xFFFF
        checksum=(0x10000 - s) & 0xFFFF
        struct.pack_into("<H",cat,28,checksum)
        # Default entry
        cat[32]=0x88    # bootable
        cat[33]=boot_media
        struct.pack_into("<H",cat,34,0)   # load segment default
        cat[36]=0
        sector_count=max(1, (len(boot_image)+511)//512)
        struct.pack_into("<H",cat,38,sector_count)
        struct.pack_into("<I",cat,40,boot_img_sec)  # load RBA
        img[boot_cat_sec*SECTOR:(boot_cat_sec+1)*SECTOR]=cat
        img[boot_img_sec*SECTOR:boot_img_sec*SECTOR+len(boot_image)]=boot_image

    for f in files:
        if f.data: img[f.extent*SECTOR:f.extent*SECTOR+len(f.data)]=f.data
    return bytes(img), boot_cat_sec

if __name__ == "__main__":
    F=lambda n,d: Node(n,False,d)
    def D(n,ch): x=Node(n,True); x.children=ch; return x
    tree=[F("readme.txt", b"bootable disc\n"*10),
          D("tools",[F("setup.exe", bytes(range(256))*8)])]
    # A dummy no-emulation boot sector (2048 bytes) ending in 0x55AA.
    boot=bytearray(2048); boot[0:4]=b"BOOT"; boot[510]=0x55; boot[511]=0xAA
    img, cat_sec = build("BOOTDISC", tree, joliet=True, boot_image=bytes(boot), boot_media=0)
    with tempfile.NamedTemporaryFile(suffix=".iso",delete=False) as tf: tf.write(img); path=tf.name
    print(f"built {len(img)} bytes / {len(img)//SECTOR} sectors, boot catalog @ sector {cat_sec}\n")

    print("=== isoinfo -d (should report El Torito) ===")
    out=subprocess.run(["isoinfo","-d","-i",path],capture_output=True,text=True).stdout
    for line in out.splitlines():
        if any(k in line for k in ("El Torito","Joliet","Volume id","boot")): print("  "+line.strip())

    # Manual catalog verification
    cat=img[cat_sec*SECTOR:cat_sec*SECTOR+64]
    words=struct.unpack("<16H", cat[0:32])
    checksum_ok=(sum(words)&0xFFFF)==0
    valid_ok = cat[0]==1 and cat[30]==0x55 and cat[31]==0xAA
    boot_ok = cat[32]==0x88
    load_rba=struct.unpack_from("<I",cat,40)[0]
    img_ok = img[load_rba*SECTOR:load_rba*SECTOR+4]==b"BOOT"
    print(f"\n  validation entry 0x55AA + header: {valid_ok}")
    print(f"  validation checksum == 0:         {checksum_ok}")
    print(f"  default entry bootable (0x88):    {boot_ok}")
    print(f"  load RBA -> boot image found:     {img_ok} (sector {load_rba})")
    print("\nEL TORITO VALIDATION:", "PASS" if (valid_ok and checksum_ok and boot_ok and img_ok) else "FAIL")
    os.unlink(path)
