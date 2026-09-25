#!/usr/bin/env python3
# DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
# Not open source. No permission is granted to copy, fork or redistribute.
# See LICENSE at the root of this repository.
"""
Builds the ACE test archives in this folder. Nothing here compresses anything: every archive is
either cut out of a real WinAce 2.0 archive (winappdbg-winappdbg_v1.6_plain.ace from acefile-testdata,
BSD-licensed contents) or assembled from hand-built bit streams, and every one is checked with acefile
(https://github.com/droe/acefile, BSD 2-clause) as the reference decoder. manifest.txt records the
SHA-1 of every member as acefile decodes it; the C# tests compare against that.

Usage:  python3 make-fixtures.py <path-to-acefile-dir> <path-to-acefile-testdata-dir>
"""
import hashlib, io, os, random, struct, sys

ACEFILE, TESTDATA = sys.argv[1], sys.argv[2]
sys.path.insert(0, ACEFILE)
import acefile  # noqa: E402

OUT = os.path.dirname(os.path.abspath(__file__))
WINAPPDBG = os.path.join(TESTDATA, 'blocked_unregistered', 'winappdbg-winappdbg_v1.6_plain.ace')
CAFE = os.path.join(TESTDATA, 'codepage', 'ace32-cafe-filename_encoding=cp437_filenamecontains=café.ace')
PASSWORD = 'DiscForge'
DOSTIME = ((2026 - 1980) << 25) | (9 << 21) | (25 << 16) | (12 << 11)

MAIN_MULTIVOLUME, MAIN_SOLID, MAIN_COMMENT = 1 << 11, 1 << 15, 1 << 1
F_ADDSIZE, F_CONTPREV, F_CONTNEXT, F_PASSWORD, F_SOLID = 1, 1 << 12, 1 << 13, 1 << 14, 1 << 15


def crc16(b):
    return acefile.ace_crc16(b)


def header(body):
    return struct.pack('<HH', crc16(body), len(body)) + body


def main_body(flags, volume=0, comment_raw=b''):
    b = struct.pack('<BH', 0, flags) + b'**ACE**' + struct.pack('<BBBBL', 20, 20, 2, volume, DOSTIME) + bytes(8)
    b += b'\0'  # advert length
    if flags & MAIN_COMMENT:
        b += struct.pack('<H', len(comment_raw)) + comment_raw
    return b


def file_body(flags, pack, orig, crc, name, comptype=0, attribs=0x20, params=12):
    return (struct.pack('<BH', 1, flags | F_ADDSIZE) + struct.pack('<LLLLLBBHHH', pack, orig, DOSTIME, attribs, crc,
            comptype, 3 if comptype else 0, params, 0, len(name)) + name)


def parse(path):
    """Raw headers of a single-volume archive: list of dicts (type, flags, body, data)."""
    f = open(path, 'rb').read()
    pos, out = 0, []
    while pos < len(f):
        _, hsize = struct.unpack_from('<HH', f, pos)
        body = f[pos + 4:pos + 4 + hsize]
        h = {'type': body[0], 'flags': struct.unpack_from('<H', body, 1)[0], 'body': body, 'data': b''}
        pos += 4 + hsize
        if h['type'] == 1:
            pack = struct.unpack_from('<L', body, 3)[0]
            h['data'] = f[pos:pos + pack]
            pos += pack
        out.append(h)
    return out


def set_file_fields(body, flags=None, pack=None, crc=None):
    b = bytearray(body)
    if flags is not None: struct.pack_into('<H', b, 1, flags)
    if pack is not None: struct.pack_into('<L', b, 3, pack)
    if crc is not None: struct.pack_into('<L', b, 3 + 8 + 8, crc)
    return bytes(b)


def encrypt(data, pwd):
    bf = acefile.AceBlowfish(pwd.encode())
    data += bytes((-len(data)) % 8)
    out, pl, pr = [], 0, 0
    for i in range(0, len(data), 8):
        l, r = struct.unpack('<LL', data[i:i + 8])
        cl, cr = bf._bf_encrypt_block(l ^ pl, r ^ pr)
        out.append(struct.pack('<LL', cl, cr))
        pl, pr = cl, cr
    return b''.join(out)


# ------------------------------------------------------------------ bit streams (MSB-first 32-bit LE words)

class Bits:
    def __init__(self): self.bits = []

    def w(self, v, n):
        for i in range(n - 1, -1, -1): self.bits.append((v >> i) & 1)

    def data(self):
        b = self.bits + [0] * ((-len(self.bits)) % 32)
        out = bytearray()
        for i in range(0, len(b), 32):
            v = 0
            for bit in b[i:i + 32]: v = (v << 1) | bit
            out += struct.pack('<L', v)
        return bytes(out) + bytes(8)   # a little slack, as real encoders leave


def rand_code(r, nsym, maxw):
    ws = [1, 1]
    while len(ws) < nsym:
        cand = [i for i, w in enumerate(ws) if w < maxw]
        if not cand: break
        w = ws.pop(r.choice(cand)); ws += [w + 1, w + 1]
    return ws


def codes_for(widths, maxw):
    t = acefile.Huffman._make_tree(list(widths), maxw)
    codes = t.codes
    return {s: (codes.index(s) >> (maxw - widths[s]), widths[s]) for s in range(len(widths)) if widths[s] > 0}


def write_tree(bw, r, W, maxw):
    nz = [w for w in W if w > 0]
    lower = min(nz) - 1; U = max(nz) - lower + 1
    t = [(w - lower) if w > 0 else 0 for w in W]
    s = [t[0]] + [(t[i] - t[i - 1]) % U for i in range(1, len(t))]
    bw.w(len(W) - 1, 9); bw.w(lower, 4); bw.w(U, 4)
    ww = rand_code(r, U + 1, 7); r.shuffle(ww)
    for x in ww: bw.w(x, 3)
    wenc = codes_for(ww, 7)
    i = 0
    while i < len(s):
        if s[i] == 0:
            j = i
            while j < len(s) and s[j] == 0 and j - i < 19: j += 1
            if j - i >= 4:
                c, n = wenc[U]; bw.w(c, n); bw.w(j - i - 4, 4); i = j; continue
        c, n = wenc[s[i]]; bw.w(c, n); i += 1
    return codes_for(W, maxw)


def widths_for(r, symbols, maxw):
    syms = sorted(set(symbols))
    ws = rand_code(r, max(len(syms), 2), maxw)
    if len(syms) < 2: syms = syms + [max(syms) + 1]
    W = [0] * (max(syms) + 1)
    r.shuffle(ws)
    for s, w in zip(syms, ws): W[s] = w
    return W


def lz77_stream(r, n, typecode_then=None, bw=None):
    """LZ77 symbols producing n bytes: literals and back-references with explicit distances.
    If typecode_then is given, end with the mode-switch symbol followed by that mode byte."""
    bw = bw or Bits()
    ops, have = [], 0
    while have < n:
        if have > 8 and r.random() < 0.35:
            d = r.randint(0, min(have - 1, 40000))
            bits = d.bit_length()
            minlen = 2 if d <= 255 else 3 if d <= 8191 else 4
            ln = r.randint(minlen, min(minlen + 60, minlen + n - have - minlen) if n - have > minlen else minlen)
            if have + ln > n: ops.append(('lit', r.choice(b'ACE DiscForge\n'))); have += 1; continue
            ops.append(('copy', bits, d, ln - minlen)); have += ln
        else:
            ops.append(('lit', r.choice(b'ACE DiscForge WinAce 2.0 \x00\xff\n'))); have += 1
    main_syms = [o[1] if o[0] == 'lit' else 260 + o[1] for o in ops]
    if typecode_then is not None: main_syms.append(283)
    len_syms = [o[3] for o in ops if o[0] == 'copy'] or [0]
    mainW = widths_for(r, main_syms, 11)
    lenW = widths_for(r, len_syms, 11)
    menc = write_tree(bw, r, mainW, 11)
    lenc = write_tree(bw, r, lenW, 11)
    bw.w(len(main_syms), 15)
    for o in ops:
        if o[0] == 'lit':
            c, k = menc[o[1]]; bw.w(c, k)
        else:
            _, bits, d, L = o
            c, k = menc[260 + bits]; bw.w(c, k)
            if bits >= 2: bw.w(d - (1 << (bits - 1)), bits - 1)
            c, k = lenc[L]; bw.w(c, k)
    if typecode_then is not None:
        c, k = menc[283]; bw.w(c, k); bw.w(typecode_then, 8)
    return bw


def sound_stream(r, bw, mode, nsyms):
    nmodels = [1, 2, 3, 3][mode - 3] * 3
    left = nsyms
    while left > 0:
        W = widths_for(r, r.sample(range(288), r.choice([6, 40, 200])) + list(range(0, 32, 5)), 10)
        trees = [write_tree(bw, r, W, 10) for _ in range(nmodels)]
        cnt = min(left, r.choice([50, 500, 3000]))
        bw.w(cnt, 15)
        syms = [s for s in trees[0] if s != 288]
        for _ in range(cnt):
            c, k = trees[0][r.choice(syms)]; bw.w(c, k)
        left -= cnt


class _PicDriver:
    """Stands in for acefile's BitStream while acefile's own PIC decoder runs: every value the decoder
    asks for is chosen here and written to *bw*, so the result decodes back to exactly this."""
    def __init__(self, r, bw, width, planes, rows):
        self.r, self.bw, self.rows = r, bw, rows
        self.fixed = [width, planes]

    def read_bits(self, n):
        if n == 1:                       # "another row follows?"
            v = 1 if self.rows > 0 else 0
            self.rows -= 1
        elif n == 2: v = self.r.choice([0, 1, 2])   # pixel decoder for planes 1..N
        elif n == 8: v = 0                          # mode after the PIC block: back to LZ77
        else: raise AssertionError(n)
        self.bw.w(v, n)
        return v

    def read_golomb_rice(self, r_bits, signed=False):
        if self.fixed: v = self.fixed.pop(0)
        else: v = min(int(self.r.expovariate(1 / max(1, (1 << r_bits)))) , 4000)
        if r_bits: self.bw.w(v & ((1 << r_bits) - 1), r_bits)
        for _ in range(v >> r_bits): self.bw.w(1, 1)
        self.bw.w(0, 1)
        if not signed: return v
        return -(v >> 1) - 1 if v & 1 else v >> 1


def pic_stream(r, bw, width, planes, rows):
    d = _PicDriver(r, bw, width, planes, rows)
    pic = acefile.Pic()
    pic.reinit(d)
    chunk, mode = pic.read(d, width * rows + 1)
    assert mode is not None and mode.mode == 0 and len(chunk) == width * rows


def decode_blocked(packed, size):
    return b''.join(acefile.ACE().decompress_blocked(io.BytesIO(packed), size, 1 << 22))


def decode_lz77(packed, size):
    return b''.join(acefile.ACE().decompress_lz77(io.BytesIO(packed), size, 1 << 22))


def write(name, *volumes):
    for i, v in enumerate(volumes):
        fn = name if i == 0 else name[:-4] + '.c%02d' % (i - 1)
        open(os.path.join(OUT, fn), 'wb').write(v)


# ------------------------------------------------------------------ 1. solid prefix of a real archive

src = parse(WINAPPDBG)
prefix = src[:1 + 35]   # main header + 35 members (21 folders, stored and compressed files, ~11 KB)
solid = b''.join(header(h['body']) + h['data'] for h in prefix)
write('winace-solid.ace', solid)

# ------------------------------------------------------------------ 2. the same, every file encrypted

enc = b''
for h in prefix:
    if h['type'] == 1 and len(h['data']) > 0:
        data = encrypt(h['data'], PASSWORD)
        enc += header(set_file_fields(h['body'], flags=h['flags'] | F_PASSWORD, pack=len(data))) + data
    else:
        enc += header(h['body']) + h['data']
write('winace-solid-password.ace', enc)

# ------------------------------------------------------------------ 3. the same, split over three volumes

limit, vols, cur = 4000, [], None
mflags = prefix[0]['flags'] | MAIN_MULTIVOLUME
def new_volume():
    global cur
    if cur is not None: vols.append(cur)
    cur = bytearray(header(main_body(mflags & ~(MAIN_COMMENT | (1 << 12)), len(vols))))
new_volume()
for h in prefix[1:]:
    data, first = h['data'], True
    while True:
        room = max(limit - len(cur) - len(h['body']) - 4, 1)
        part, data = data[:room], data[room:]
        fl = h['flags'] | (0 if first else F_CONTPREV) | (F_CONTNEXT if data else 0)
        crc = struct.unpack_from('<L', h['body'], 19)[0] if not data else 0   # only the last part has the CRC
        cur += header(set_file_fields(h['body'], flags=fl, pack=len(part), crc=crc)) + part
        first = False
        if not data: break
        new_volume()
    if len(cur) >= limit: new_volume()
vols.append(cur)
write('winace-multi.ace', *[bytes(v) for v in vols])

# ------------------------------------------------------------------ 4. hand-built streams: ACE 1.0 LZ77, SOUND, PIC

r = random.Random(20260925)
members = []
# ACE 1.0 (comptype 1)
bw = lz77_stream(r, 6000); packed = bw.data(); out = decode_lz77(packed, 6000)
members.append((b'lz77-ace10.bin', 1, packed, out))
# ACE 2.0 blocked: LZ77 then a switch to each SOUND mode
for mode in (3, 4, 5, 6):
    bw = lz77_stream(r, 500, typecode_then=mode)
    sound_stream(r, bw, mode, 9000)
    packed = bw.data()
    size = 500 + 4000
    out = decode_blocked(packed, size)
    members.append((b'sound-mode%d.bin' % mode, 2, packed, out))
# ACE 2.0 blocked: LZ77 then PIC
for width, planes in ((61, 1), (90, 3), (128, 4)):
    bw = lz77_stream(r, 300, typecode_then=7)
    pic_stream(r, bw, width, planes, 24)   # ends with the switch back to LZ77…
    lz77_stream(r, 200, bw=bw)             # …whose first block used up its symbol count, so new trees follow
    packed = bw.data()
    size = 300 + width * 24 + 200
    out = decode_blocked(packed, size)
    members.append((b'pic-%dx%d.bin' % (width, planes), 2, packed, out))

arc = header(main_body(0))
for name, ct, packed, out in members:
    arc += header(file_body(0, len(packed), len(out), acefile.ace_crc32(out), name, comptype=ct)) + packed
write('synthetic-modes.ace', arc)

# ------------------------------------------------------------------ 5. hostile names (all stored)

names = [b'../../escape-1.txt', b'..\\..\\escape-2.txt', b'C:\\Windows\\escape-3.txt', b'/etc/escape-4.txt',
         b'\\\\server\\share\\escape-5.txt', b'safe/../../escape-6.txt', b'CON.txt', b'aux', b'dots.../x.txt',
         b'a\x05b?c*.txt', b'...', b'good/inner/file.txt']
arc = header(main_body(0))
for i, n in enumerate(names):
    body = ('file %d\n' % i).encode()
    arc += header(file_body(0, len(body), len(body), acefile.ace_crc32(body), n)) + body
write('hostile-names.ace', arc)

# ------------------------------------------------------------------ 6. cp437 filename (copied as is)

open(os.path.join(OUT, 'cp437-cafe.ace'), 'wb').write(open(CAFE, 'rb').read())

# ------------------------------------------------------------------ manifest via acefile

lines = []
for fn in sorted(os.listdir(OUT)):
    if not fn.endswith('.ace'): continue
    with acefile.open(os.path.join(OUT, fn)) as a:
        for m in a.getmembers():
            if m.is_dir():
                lines.append('%s|%s|dir' % (fn, m.raw_filename.decode('latin-1')))
                continue
            data = a.read(m, pwd=PASSWORD if m.is_enc() else None)
            lines.append('%s|%s|%d|%s' % (fn, m.raw_filename.decode('latin-1'), len(data), hashlib.sha1(data).hexdigest()))
open(os.path.join(OUT, 'manifest.txt'), 'w', newline='\n').write('\n'.join(lines) + '\n')
print('\n'.join(lines[-20:]))
print(len(lines), 'members')
