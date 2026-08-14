#!/usr/bin/env python3
"""
ISO 9660 / Joliet / Rock Ridge *reader* — reference oracle.

The mirror of iso_build*.py: given an image, work out what files are on it.
Validated against `isoinfo` on both our own builder's output and a real ISO.

Reading is fiddlier than writing because the image chooses the rules:
  - the PVD (sector 16) describes the 8.3 hierarchy;
  - a type-2 SVD with a '%/E' escape describes a parallel Joliet tree with
    UCS-2 names — prefer it when present;
  - Rock Ridge hides POSIX names in the System Use area *after* the identifier,
    in an 'NM' entry, which must be found by walking SUSP entries;
  - a directory record with length 0 means "skip to the next sector boundary",
    not "end of directory".
"""
import struct, sys, os, subprocess, tempfile

SECTOR = 2048


def u32le(b, o): return struct.unpack_from("<I", b, o)[0]
def u16le(b, o): return struct.unpack_from("<H", b, o)[0]


class Iso:
    def __init__(self, data):
        self.data = data

    def sector(self, lba):
        off = lba * SECTOR
        return self.data[off:off + SECTOR]

    def find_descriptors(self):
        """Walk the volume descriptor set from sector 16 to the terminator."""
        pvd = None
        svd = None
        lba = 16
        while True:
            s = self.sector(lba)
            if len(s) < 7 or s[1:6] != b"CD001":
                break
            t = s[0]
            if t == 0xFF:
                break
            if t == 1 and pvd is None:
                pvd = s
            elif t == 2:
                # Joliet is a supplementary descriptor whose escape sequence
                # selects UCS-2. %/@ %/C %/E are the three levels.
                esc = s[88:91]
                if esc in (b"%/@", b"%/C", b"%/E"):
                    svd = s
            lba += 1
            if lba > 100:
                break
        if pvd is None:
            raise ValueError("no primary volume descriptor (not an ISO 9660 image?)")
        return pvd, svd

    def volume_id(self, desc, joliet):
        raw = desc[40:72]
        if joliet:
            return raw.decode("utf-16-be", "ignore").rstrip(" \x00")
        return raw.decode("ascii", "ignore").rstrip(" ")

    def root_record(self, desc):
        return desc[156:190]

    def read_dir(self, extent, length, joliet, rock_ridge):
        """Read every record in a directory extent."""
        data = self.data[extent * SECTOR: extent * SECTOR + length]
        entries = []
        p = 0
        while p < len(data):
            rec_len = data[p]
            if rec_len == 0:
                # Zero length = padding to the next sector boundary, NOT the end.
                next_p = ((p // SECTOR) + 1) * SECTOR
                if next_p <= p:
                    break
                p = next_p
                continue
            if p + rec_len > len(data):
                break

            rec = data[p:p + rec_len]
            ext = u32le(rec, 2)
            size = u32le(rec, 10)
            flags = rec[25]
            id_len = rec[32]
            ident = rec[33:33 + id_len]

            is_dir = bool(flags & 0x02)
            # '.' and '..' are single 0x00 / 0x01 bytes.
            if id_len == 1 and ident in (b"\x00", b"\x01"):
                p += rec_len
                continue

            name = self.decode_name(ident, joliet)
            if rock_ridge:
                nm = self.rock_ridge_name(rec, id_len)
                if nm:
                    name = nm

            entries.append({"name": name, "extent": ext, "size": size, "is_dir": is_dir})
            p += rec_len
        return entries

    @staticmethod
    def decode_name(ident, joliet):
        if joliet:
            n = ident.decode("utf-16-be", "ignore")
        else:
            n = ident.decode("ascii", "ignore")
        # Strip the ';1' version suffix, then a bare trailing dot.
        if ";" in n:
            n = n.split(";")[0]
        if n.endswith(".") and len(n) > 1:
            n = n[:-1]
        return n

    @staticmethod
    def rock_ridge_name(rec, id_len):
        """Walk SUSP entries in the System Use area for an NM entry."""
        base = 33 + id_len
        if base % 2:
            base += 1              # identifier is padded to an even boundary
        su = rec[base:]
        name = None
        p = 0
        while p + 4 <= len(su):
            sig = su[p:p + 2]
            ln = su[p + 2]
            if ln < 4 or p + ln > len(su):
                break
            if sig == b"NM":
                # byte 4 is flags; bit 0 = CONTINUE
                name = (name or "") + su[p + 5:p + ln].decode("utf-8", "ignore")
            p += ln
        return name

    def walk(self, joliet=None, rock_ridge=True):
        pvd, svd = self.find_descriptors()
        use_joliet = svd is not None if joliet is None else joliet
        desc = svd if (use_joliet and svd is not None) else pvd
        # Rock Ridge lives in the primary tree only.
        rr = rock_ridge and desc is pvd

        root = self.root_record(desc)
        extent = u32le(root, 2)
        length = u32le(root, 10)

        out = []

        def recurse(ext, ln, prefix):
            for e in self.read_dir(ext, ln, use_joliet and desc is svd, rr):
                path = prefix + "/" + e["name"]
                out.append({**e, "path": path})
                if e["is_dir"]:
                    recurse(e["extent"], e["size"], path)

        recurse(extent, length, "")
        return {"volume": self.volume_id(desc, use_joliet and desc is svd),
                "joliet": use_joliet and svd is not None,
                "entries": out}

    def read_file(self, extent, size):
        return self.data[extent * SECTOR: extent * SECTOR + size]


if __name__ == "__main__":
    from importlib import util
    ok = True

    def load(mod, path):
        sp = util.spec_from_file_location(mod, path)
        m = util.module_from_spec(sp)
        sp.loader.exec_module(m)
        return m

    here = os.path.dirname(__file__)
    jol = load("j", os.path.join(here, "iso_build_joliet.py"))
    rr = load("r", os.path.join(here, "iso_build_rockridge.py"))

    # --- Case 1: our Joliet builder's output, read back ---
    F = lambda n, d: jol.Node(n, False, d)
    def D(n, ch):
        x = jol.Node(n, True); x.children = ch; return x

    tree = [F("Read Me First.txt", b"hello\n" * 20),
            D("Saved Games", [F("Level 3 - Boss.sav", b"BOSS" * 40),
                              D("Backups", [F("auto-2026.sav", b"AUTO" * 60)])])]
    img = jol.build("My Game Disc", tree, joliet=True)
    iso = Iso(img)
    res = iso.walk()
    paths = sorted(e["path"] for e in res["entries"])
    expect = sorted(["/Read Me First.txt", "/Saved Games", "/Saved Games/Level 3 - Boss.sav",
                     "/Saved Games/Backups", "/Saved Games/Backups/auto-2026.sav"])
    c1 = paths == expect and res["joliet"]
    print(f"  joliet long names read back : {'OK' if c1 else 'FAIL'}")
    if not c1:
        print(f"    got {paths}")
    ok &= c1

    # file content round-trip
    e = next(x for x in res["entries"] if x["path"] == "/Saved Games/Backups/auto-2026.sav")
    c1b = iso.read_file(e["extent"], e["size"]) == b"AUTO" * 60
    print(f"  file content round-trip     : {'OK' if c1b else 'FAIL'}")
    ok &= c1b

    # --- Case 2: Rock Ridge POSIX names ---
    Fr = lambda n, d: rr.Node(n, False, d)
    def Dr(n, ch):
        x = rr.Node(n, True); x.children = ch; return x
    tree2 = [Fr("my-archive.tar.gz", b"payload\n" * 30),
             Dr("src-files", [Fr("main.c", b"int main(){}\n" * 8)])]
    img2 = rr.build("RRDISC", tree2)
    res2 = Iso(img2).walk(joliet=False, rock_ridge=True)
    paths2 = sorted(e["path"] for e in res2["entries"])
    c2 = paths2 == sorted(["/my-archive.tar.gz", "/src-files", "/src-files/main.c"])
    print(f"  rock ridge names read back  : {'OK' if c2 else 'FAIL'}")
    if not c2:
        print(f"    got {paths2}")
    ok &= c2

    # --- Case 3: the 8.3 fallback view of the same image ---
    # splitext("my-archive.tar.gz") -> base "my-archive.tar", ext "gz",
    # so Level 1 gives MY_ARCHI.GZ — the last extension wins, not ".tar".
    res3 = Iso(img2).walk(joliet=False, rock_ridge=False)
    paths3 = sorted(e["path"] for e in res3["entries"])
    c3 = "/MY_ARCHI.GZ" in paths3 and "/SRC_FILE" in paths3
    print(f"  8.3 fallback view           : {'OK' if c3 else 'FAIL'}  {paths3[:2]}")
    ok &= c3

    # --- Case 4: a REAL iso, cross-checked against isoinfo ---
    real = os.path.join(here, "..", "..", "tests", "fixtures", "source.iso")
    if os.path.exists(real):
        data = open(real, "rb").read()
        res4 = Iso(data).walk()
        ours = sorted(e["path"] for e in res4["entries"] if not e["is_dir"])

        out = subprocess.run(["isoinfo", "-f", "-i", real], capture_output=True, text=True).stdout
        theirs = sorted(l.strip() for l in out.splitlines() if l.strip() and not l.endswith(";1") is False)
        # isoinfo -f lists both dirs and files; take everything with a version suffix
        theirs = sorted(l.strip().split(";")[0] for l in out.splitlines() if ";1" in l)

        c4 = ours == theirs
        print(f"  REAL iso vs isoinfo         : {'OK' if c4 else 'FAIL'}")
        print(f"    ours   : {ours}")
        print(f"    isoinfo: {theirs}")
        ok &= c4
    else:
        print("  REAL iso                    : skipped (fixture not found)")

    print("\nISO READER:", "PASS" if ok else "FAIL")
    sys.exit(0 if ok else 1)
