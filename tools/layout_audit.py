#!/usr/bin/env python3
"""Static WinForms layout audit for DiscForge views.

WinForms control coordinates are RELATIVE TO THE IMMEDIATE PARENT, so a valid
overlap check may only compare controls that share:
  * the same class scope   (a .cs file can hold a view + several dialog classes)
  * the same parent container (the form `this`, or a specific GroupBox/Panel)
  * simultaneous visibility (mode-toggled overlays set Visible = false)

This script extracts each control's rectangle, resolves its class and parent,
skips declared-hidden overlay controls, and flags any two SIMULTANEOUSLY-VISIBLE
same-parent controls whose rectangles overlap in both axes — the class of bug
behind the Disc Quality Eject/Scan collision and the Split-tool combo/button
collision.
"""
import re
import sys
import glob
import os

POINT = re.compile(r'Location\s*=\s*new\s+Point\(\s*(-?\d+)\s*,\s*(-?\d+)\s*\)')
SIZE = re.compile(r'Size\s*=\s*new\s+Size\(\s*(-?\d+)\s*,\s*(-?\d+)\s*\)')
WIDTH = re.compile(r'\bWidth\s*=\s*(-?\d+)')
HEIGHT = re.compile(r'\bHeight\s*=\s*(-?\d+)')
TEXT = re.compile(r'Text\s*=\s*"((?:[^"\\]|\\.)*)"')
HIDDEN = re.compile(r'Visible\s*=\s*false')
DEFAULT_H = 24

ID_LOC = re.compile(r'([A-Za-z_]\w*)\.Location\s*=\s*new\s+Point\(\s*(-?\d+)\s*,\s*(-?\d+)\s*\)')
ID_SIZE = re.compile(r'([A-Za-z_]\w*)\.Size\s*=\s*new\s+Size\(\s*(-?\d+)\s*,\s*(-?\d+)\s*\)')
ID_W = re.compile(r'([A-Za-z_]\w*)\.Width\s*=\s*(-?\d+)')
ID_H = re.compile(r'([A-Za-z_]\w*)\.Height\s*=\s*(-?\d+)')
ID_CREATE = re.compile(r'([A-Za-z_]\w*)\s*=\s*new\s*(?:[A-Za-z0-9_<>]+\s*)?(?:\([^)]*\))?\s*\{')
# `<parent>.Controls.Add(<id>)`  and bare `Controls.Add(<id>)` (parent == this)
ADD_ID = re.compile(r'(?:([A-Za-z_]\w*)\.)?Controls\.Add\(\s*([A-Za-z_]\w*)\s*\)')
# array group-add: capture the Control[] { ... } members, added in a foreach to a parent
ARRAY = re.compile(r'new\s+Control\[\]\s*\{([^}]*)\}')


def brace_end(src, open_idx):
    depth = 0
    j = open_idx
    while j < len(src):
        if src[j] == '{':
            depth += 1
        elif src[j] == '}':
            depth -= 1
            if depth == 0:
                return j
        j += 1
    return len(src) - 1


def class_scopes(src):
    """List of (name, start, end) byte ranges for each class body."""
    out = []
    for m in re.finditer(r'\bclass\s+([A-Za-z_]\w*)', src):
        brace = src.find('{', m.end())
        if brace < 0:
            continue
        out.append((m.group(1), brace, brace_end(src, brace)))
    return out


def scope_of(pos, scopes):
    # innermost enclosing class
    best = None
    for name, s, e in scopes:
        if s <= pos <= e:
            if best is None or (s > best[1]):
                best = (name, s, e)
    return best[0] if best else '<file>'


def parent_map(src):
    """id -> parent container name. Bare Controls.Add / AddRange => 'this'."""
    pm = {}
    for m in ADD_ID.finditer(src):
        parent = m.group(1) or 'this'
        pm[m.group(2)] = parent
    # foreach group-add: find `new Control[] { a, b, ... }` then the nearest following
    #  `<parent>.Controls.Add(` within a few hundred chars.
    for m in ARRAY.finditer(src):
        members = [x.strip() for x in m.group(1).split(',') if x.strip()]
        tail = src[m.end():m.end() + 400]
        am = re.search(r'([A-Za-z_]\w*)\.Controls\.Add\(', tail)
        parent = am.group(1) if am else 'this'
        for mem in members:
            if re.fullmatch(r'[A-Za-z_]\w*', mem):
                pm.setdefault(mem, parent)
    return pm


def named_controls(src):
    recs = {}

    def rec(name, pos):
        r = recs.setdefault(name, dict(x=None, y=None, w=None, h=None,
                                       label=None, hidden=False, pos=pos))
        return r

    for m in ID_LOC.finditer(src):
        r = rec(m.group(1), m.start()); r['x'] = int(m.group(2)); r['y'] = int(m.group(3))
    for m in ID_SIZE.finditer(src):
        r = rec(m.group(1), m.start()); r['w'] = int(m.group(2)); r['h'] = int(m.group(3))
    for m in ID_W.finditer(src):
        r = rec(m.group(1), m.start())
        if r['w'] is None:
            r['w'] = int(m.group(2))
    for m in ID_H.finditer(src):
        r = rec(m.group(1), m.start())
        if r['h'] is None:
            r['h'] = int(m.group(2))
    for m in ID_CREATE.finditer(src):
        name = m.group(1)
        blk = src[m.end() - 1:brace_end(src, m.end() - 1) + 1]
        r = rec(name, m.start())
        if r['pos'] is None:
            r['pos'] = m.start()
        if r['w'] is None:
            sm = SIZE.search(blk)
            if sm:
                r['w'] = int(sm.group(1)); r['h'] = r['h'] or int(sm.group(2))
            else:
                wm = WIDTH.search(blk)
                if wm:
                    r['w'] = int(wm.group(1))
        if r['label'] is None:
            tm = TEXT.search(blk)
            if tm:
                r['label'] = tm.group(1)
        if r['x'] is None:
            pm = POINT.search(blk)
            if pm:
                r['x'] = int(pm.group(1)); r['y'] = int(pm.group(2))
        if HIDDEN.search(blk):
            r['hidden'] = True

    out = []
    for name, r in recs.items():
        if r['x'] is None or r['w'] is None:
            continue
        out.append(dict(x=r['x'], y=r['y'] if r['y'] is not None else 0,
                        w=r['w'], h=r['h'] or DEFAULT_H,
                        label=r['label'] or name, name=name,
                        hidden=r['hidden'], pos=r['pos']))
    return out


def anon_controls(src):
    """Anonymous `Controls.Add(new X { ... })` initializers — parent is the
    receiver of the enclosing Controls.Add call."""
    out = []
    for m in re.finditer(r'new\s+[A-Za-z0-9_<>]+\s*\{', src):
        i = m.end() - 1
        blk = src[m.start():brace_end(src, i) + 1]
        pm = POINT.search(blk)
        if not pm:
            continue
        w = h = None
        sm = SIZE.search(blk)
        if sm:
            w, h = int(sm.group(1)), int(sm.group(2))
        else:
            wm = WIDTH.search(blk)
            if wm:
                w = int(wm.group(1))
            hm = HEIGHT.search(blk)
            if hm:
                h = int(hm.group(1))
        if w is None:
            continue
        tm = TEXT.search(blk)
        nm = re.match(r'new\s+([A-Za-z0-9_<>]+)', blk)
        # resolve parent: nearest `<parent>.Controls.Add(` opened just before this block
        pre = src[max(0, m.start() - 120):m.start()]
        rm = re.search(r'([A-Za-z_]\w*)\.Controls\.Add(?:Range)?\(\s*$', pre) or \
            re.search(r'([A-Za-z_]\w*)\.Controls\.Add(?:Range)?\(', pre[::-1] and pre)
        parent = rm.group(1) if rm else 'this'
        out.append(dict(x=int(pm.group(1)), y=int(pm.group(2)), w=w,
                        h=h if h is not None else DEFAULT_H,
                        label=(tm.group(1) if tm else (nm.group(1) if nm else '?')),
                        name=None, hidden=bool(HIDDEN.search(blk)), pos=m.start()))
    return out


def contains(a, b):
    return (a['x'] <= b['x'] and a['y'] <= b['y'] and
            a['x'] + a['w'] >= b['x'] + b['w'] and a['y'] + a['h'] >= b['y'] + b['h'])


def audit_file(path):
    src = open(path, encoding='utf-8', errors='replace').read()
    scopes = class_scopes(src)
    pmap = parent_map(src)

    ctrls = named_controls(src) + anon_controls(src)
    # attach class scope + parent + dedupe
    seen = set()
    prepared = []
    for c in ctrls:
        key = (c['x'], c['y'], c['w'], c['h'], c['label'], c['pos'])
        if key in seen:
            continue
        seen.add(key)
        c['scope'] = scope_of(c['pos'], scopes) if c['pos'] is not None else '<file>'
        c['parent'] = pmap.get(c['name'], 'this') if c['name'] else c.get('parent', 'this')
        prepared.append(c)

    findings = []
    for i in range(len(prepared)):
        for j in range(i + 1, len(prepared)):
            a, b = prepared[i], prepared[j]
            if a['scope'] != b['scope']:
                continue                      # different class = different form
            if a['parent'] != b['parent']:
                continue                      # different container = different coord space
            if a['hidden'] or b['hidden']:
                continue                      # mode-toggled overlay, not simultaneous
            if contains(a, b) or contains(b, a):
                continue                      # parent/child
            vo = min(a['y'] + a['h'], b['y'] + b['h']) - max(a['y'], b['y'])
            ho = min(a['x'] + a['w'], b['x'] + b['w']) - max(a['x'], b['x'])
            if vo >= 6 and ho >= 4:
                findings.append((a, b, ho))
    return prepared, findings


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else '/root/DiscForge/src/DiscForge.App'
    files = sorted(glob.glob(os.path.join(root, '**', '*.cs'), recursive=True))
    total = 0
    for f in files:
        ctrls, findings = audit_file(f)
        if findings:
            print(f"\n=== {os.path.relpath(f, root)} ===")
            for a, b, ox in findings:
                print(f"  OVERLAP {ox}px in [{a['scope']}/{a['parent']}]: "
                      f"'{a['label']}' @x{a['x']}..{a['x']+a['w']} y{a['y']}..{a['y']+a['h']}  <->  "
                      f"'{b['label']}' @x{b['x']}..{b['x']+b['w']} y{b['y']}..{b['y']+b['h']}")
            total += len(findings)
    print(f"\n---\nReal same-parent, same-scope, both-visible overlaps: {total}")


if __name__ == '__main__':
    main()
