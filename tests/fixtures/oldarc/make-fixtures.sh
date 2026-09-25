#!/usr/bin/env bash
# DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
# Not open source. No permission is granted to copy, fork or redistribute.
# See LICENSE at the root of this repository.
#
# Builds the LHA/LArc, ARJ and ZOO test archives in this folder and manifest.txt.
#
#   LHA/LArc: copied from Lhasa's test corpus (https://github.com/fragglet/lhasa, test/archives),
#             archives made by the original DOS/Amiga/Unix tools; their contents are licence texts.
#             The checkout must be built (./autogen.sh && ./configure && make): its src/lha is the
#             reference, since older packaged lhasa releases mis-read amiga122-lh0-dirs.lzh.
#   ARJ:      made here with ARJ 3.10 (the open-source ARJ archiver, Debian package "arj").
#   ZOO:      made here with zoo 2.10 (Debian/Ubuntu package "zoo").
#
# manifest.txt lists, per archive, the size and SHA-1 of every file as the reference tool extracts
# it (lhasa for LHA, arj for ARJ, zoo for ZOO), sorted — the C# tests compare against it.
#
# Usage: make-fixtures.sh <lhasa-checkout> <zoo binary>
set -euo pipefail
LHASA=$1; ZOO=$2
OUT=$(cd "$(dirname "$0")" && pwd)
T=$(mktemp -d)
cd "$OUT"
rm -f ./*.lzh ./*.lzs ./*.run ./*.arj ./*.a0? ./*.zoo manifest.txt

A=$LHASA/test/archives
cp $A/lharc113/lh1.lzh        lharc113-lh1.lzh
cp $A/lharc113/long.lzh       lharc113-lh1-long.lzh
cp $A/larc333/lz4.lzs         larc333-lz4.lzs
cp $A/larc333/lz5.lzs         larc333-lz5.lzs
cp $A/generated/lzs/lzs.lzs   generated-lzs.lzs
cp $A/lha_amiga_122/lh4.lzh   amiga122-lh4.lzh
cp $A/lha213/lh5.lzh          lha213-lh5.lzh
cp $A/lha_unix114i/h0_lh6.lzh unix114i-h0-lh6.lzh
cp $A/lha_unix114i/h1_lh7.lzh unix114i-h1-lh7.lzh
cp $A/lha_unix114i/h2_lh5.lzh unix114i-h2-lh5.lzh
cp $A/unlha32/h2_lhx.lzh      unlha32-h2-lhx.lzh
cp $A/lhark04d/lh7.lzh        lhark04d-lh7.lzh
cp $A/lha_osk_201/h2_lh5.lzh  osk201-h2-lh5.lzh
cp $A/lha_amiga_122/sfx.run   amiga122-sfx.run
cp $A/lha_amiga_122/lh0_dirs_bug.lzh amiga122-lh0-dirs.lzh

# Content for ARJ and ZOO: text, a folder, and incompressible bytes (fixed seed).
mkdir -p "$T/in/docs"
python3 - "$T/in" <<'PY'
import random, sys, os
d = sys.argv[1]
r = random.Random(1993)
open(os.path.join(d, 'readme.txt'), 'w').write(''.join(f'Line {i}: DiscForge old-archive test, ARJ and ZOO.\n' for i in range(900)))
open(os.path.join(d, 'docs', 'notes.txt'), 'w').write('The quick brown fox jumps over the lazy dog.\n' * 120)
open(os.path.join(d, 'noise.bin'), 'wb').write(bytes(r.getrandbits(8) for _ in range(24000)))
open(os.path.join(d, 'tiny.txt'), 'w').write('x')
PY
( cd "$T/in"
  for m in 0 1 2 3 4; do arj a -r -m$m -y "$OUT/arj-m$m.arj" '*' >/dev/null; done
  arj a -r -m1 -gDiscForge -y "$OUT/arj-garbled.arj" '*' >/dev/null
  arj a -r -m1 -v10k -y "$OUT/arj-multi.arj" '*' >/dev/null
  find . -type f | sed 's|^\./||' | sort > "$T/list"
  "$ZOO" aI "$OUT/zoo-lzw.zoo" < "$T/list" >/dev/null
  "$ZOO" ahI "$OUT/zoo-lzh.zoo" < "$T/list" >/dev/null
  "$ZOO" afI "$OUT/zoo-stored.zoo" < "$T/list" >/dev/null )

sums() { (cd "$1" && find . -type f -exec sh -c 'printf "%s|%s\n" "$(stat -c %s "$1")" "$(sha1sum < "$1" | cut -c1-40)"' _ {} \; | sort); }
for f in *.lzh *.lzs *.run; do
  mkdir -p "$T/x/$f"; (cd "$T/x/$f" && "$LHASA/src/lha" xq "$OUT/$f" >/dev/null)
  sums "$T/x/$f" | sed "s|^|$f|" >> manifest.txt
done
for f in arj-m0.arj arj-m1.arj arj-m2.arj arj-m3.arj arj-m4.arj arj-garbled.arj arj-multi.arj; do
  mkdir -p "$T/x/$f"; (cd "$T/x/$f" && arj x -v -y -gDiscForge "$OUT/$f" >/dev/null)
  sums "$T/x/$f" | sed "s/^/$f|/" >> manifest.txt
done
for f in zoo-*.zoo; do
  mkdir -p "$T/x/$f"; (cd "$T/x/$f" && "$ZOO" x// "$OUT/$f" >/dev/null)
  sums "$T/x/$f" | sed "s/^/$f|/" >> manifest.txt
done
sed -i 's/^\([^|]*\.lz[hs]\|[^|]*\.run\)/\1|/' manifest.txt
rm -rf "$T"
wc -l manifest.txt
