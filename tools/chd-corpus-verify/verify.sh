#!/usr/bin/env bash
# DiscForge — proprietary. Copyright (c) 2026 Andy. All rights reserved.
# Not open source. See LICENSE at the root of this repository.
#
# CHD corpus verification: cross-checks DiscForge's CHD read/write paths against
# chdman (the MAME reference tool) as an oracle. Two directions per fixture:
#
#   READ    chdman creates a CHD  ->  DiscForge extracts  ->  must equal the source
#   WRITE   DiscForge creates a CHD -> chdman verify passes AND chdman extract == source
#
# Requires: dotnet 8 SDK, chdman on PATH. Run from anywhere; paths are resolved
# relative to this script. Exits non-zero if any check fails.
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
DRIVER="$HERE/bin/Release/net8.0/chd-driver"

command -v chdman >/dev/null || { echo "chdman not found on PATH"; exit 3; }
echo "Building driver..."
dotnet build "$HERE/Driver.csproj" -c Release -v quiet >/dev/null || { echo "driver build failed"; exit 3; }

pass=0; fail=0
ok()   { pass=$((pass+1)); }
bad()  { fail=$((fail+1)); echo "  FAIL: $1"; }

# ---- gen a raw hard-disk image of N 4096-byte hunks with a named pattern -------
gen_hd() { # <file> <hunks> <pattern>
  python3 - "$1" "$2" "$3" <<'PY'
import sys,random
path,hunks,pat=sys.argv[1],int(sys.argv[2]),sys.argv[3]
HB=4096; out=bytearray()
rnd=random.Random(1234)
for h in range(hunks):
    if pat=="zeros": out+=bytes(HB)
    elif pat=="ramp": out+=bytes((i+h*7)%200 for i in range(HB))
    elif pat=="random": out+=bytes(rnd.randrange(256) for _ in range(HB))
    elif pat=="dup": out+= (out[:HB] if h else bytes((i*3)%251 for i in range(HB)))
    elif pat=="mixed":
        if h%4==0: out+=bytes(HB)
        elif h%4==1: out+=bytes((i+h)%200 for i in range(HB))
        elif h%4==2: out+=bytes(rnd.randrange(256) for _ in range(HB))
        else: out+= (out[h%4*HB:h%4*HB+HB] if h>=4 else bytes((i*5)%240 for i in range(HB)))
open(path,'wb').write(out)
PY
}

echo "== READ: chdman creates, DiscForge extracts =="
for pat in zeros ramp random dup mixed; do
  # Compressed configs plus a wholly uncompressed CHD (chdman --compression none),
  # which uses a flat offset map rather than the compressed-map bitstream — DiscForge
  # reads both. The 'random' pattern also forces NONE *hunks* inside the compressed
  # CHDs, covering that map path too.
  for comp in "none" "zlib" "zlib,flac" "lzma" "flac,zlib,huff" "huff,zlib"; do
    gen_hd "$WORK/src.img" 16 "$pat"
    tag="hd/$pat/[$comp]"
    cflag=(--compression "$comp")
    rm -f "$WORK/ref.chd"
    if ! chdman createraw -i "$WORK/src.img" -o "$WORK/ref.chd" --hunksize 4096 --unitsize 512 "${cflag[@]}" -f >/dev/null 2>&1; then
      bad "$tag (chdman createraw failed)"; continue
    fi
    if "$DRIVER" extract "$WORK/ref.chd" "$WORK/out.img" >/dev/null 2>"$WORK/err"; then
      if cmp -s "$WORK/src.img" "$WORK/out.img"; then ok; else bad "$tag (extract mismatch)"; fi
    else bad "$tag (DiscForge extract: $(cat "$WORK/err"))"; fi
  done
done

echo "== WRITE: DiscForge creates HD, chdman verifies + extracts =="
for pat in zeros ramp random dup mixed; do
  gen_hd "$WORK/src.img" 16 "$pat"
  tag="hd-write/$pat"
  if ! "$DRIVER" createhd "$WORK/src.img" "$WORK/df.chd" 2>"$WORK/err"; then
    bad "$tag (DiscForge createhd: $(cat "$WORK/err"))"; continue; fi
  if ! chdman verify -i "$WORK/df.chd" >/dev/null 2>&1; then bad "$tag (chdman verify)"; continue; fi
  rm -f "$WORK/back.img"
  if ! chdman extractraw -i "$WORK/df.chd" -o "$WORK/back.img" -f >/dev/null 2>&1; then
    bad "$tag (chdman extractraw)"; continue; fi
  if cmp -s "$WORK/src.img" "$WORK/back.img"; then ok; else bad "$tag (chdman extract mismatch)"; fi
done

echo "== WRITE (CD): DiscForge creates CD, DiscForge round-trips =="
python3 - "$WORK" <<'PY'
import os,random
w=os.sys.argv[1]; rnd=random.Random(99)
data=bytes((i*3)%251 for i in range(2352*40))
open(os.path.join(w,"disc.bin"),'wb').write(data)
open(os.path.join(w,"disc.cue"),'w').write('FILE "disc.bin" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n')
PY
if "$DRIVER" createcd "$WORK/disc.cue" "$WORK/cd.chd" 2>"$WORK/err"; then
  if chdman verify -i "$WORK/cd.chd" >/dev/null 2>&1; then
    "$DRIVER" extract "$WORK/cd.chd" "$WORK/cd.bin" >/dev/null 2>&1
    if cmp -s "$WORK/disc.bin" "$WORK/cd.bin"; then ok; else bad "cd-write (round-trip mismatch)"; fi
  else bad "cd-write (chdman verify)"; fi
else bad "cd-write (DiscForge createcd: $(cat "$WORK/err"))"; fi

echo "-----------------------------------------------"
echo "CHD corpus verification: pass=$pass fail=$fail"
[ "$fail" -eq 0 ] && echo "ALL CLEAN" || echo "FAILURES PRESENT"
exit $([ "$fail" -eq 0 ] && echo 0 || echo 1)
