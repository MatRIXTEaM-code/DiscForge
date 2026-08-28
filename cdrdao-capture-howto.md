# Building real cdrdao on Windows (MSYS2)

This doc originally walked through building cdrdao from source to
cross-check DiscForge's raw-DAO burn engine against the reference
implementation. **That investigation is closed** — see the outcome below —
but the build steps stay useful any time a future session needs a real
`cdrdao.exe` on Windows (comparing against another tool's SCSI trace,
checking a new drive's behavior, etc.), so this doc is kept as a build
reference rather than deleted.

## What this was for, and what came of it

The question was whether cdrdao's own SAO (Session-At-Once) write path could
get a drive to accept SEND CUE SHEET, as an independent check against
DiscForge's `TestCue()` diagnostic rejecting it. Built cdrdao for real and
ran it against the same TSSTcorp SH-224DB with full verbosity
(`cdrdao.exe simulate --device D --driver generic-mmc -v 4`): **cdrdao's own
SAO write path failed identically** — same drive/firmware limitation,
confirmed independently, not a DiscForge bug. That closed the SAO/SEND CUE
SHEET avenue for good.

Separately (and this is the part that mattered), DiscForge's own **Raw**
write path (`burn-raw --engine spti`, Write Type = Raw, no cue sheet at all —
a different code path than the one above) was fixed and proven correct on
this exact hardware: a burned disc verified byte-for-byte against the golden
image (99.99%+ identical, the residual differences traced to expected
drive/read-path noise, not burn defects — see `docs/NEXT.md`'s 2026-08-27
entries for the full account). So the practical outcome: cdrdao's SAO path is
a dead end on this drive, and it didn't need to be — DiscForge's own Raw path
works.

## Building cdrdao from source, if you need it again

cdrdao officially supports Windows via MSYS2 (its own `INSTALL` file says
so). The one non-obvious part: **use the plain MSYS2 environment, not
MINGW64.**

### 1. Install MSYS2

Download and run the installer from https://www.msys2.org/ (default options
are fine).

### 2. Open the right shell

From the Start menu, open **"MSYS2 MSYS"** — the plain shell, *not* "MSYS2
MINGW64" and not MINGW32.

This matters because cdrdao's source is Linux-first and needs real POSIX
`fork()` and POSIX headers that MINGW64 doesn't provide (MinGW-w64 is a thin
Windows-native layer, not a POSIX environment). MSYS/Cygwin's MSYS
environment has genuine POSIX emulation, which is what the build actually
needs. The tradeoff is one small header-order fix in step 5.

### 3. Install build tools

In the MSYS2 MSYS shell:

```
pacman -Syu
```

It will likely ask you to close and reopen the terminal partway through — do
that, reopen "MSYS2 MSYS" (not MINGW64), then run it again until it reports
nothing more to update. Then:

```
pacman -S --needed base-devel gcc git autoconf automake libtool libiconv-devel
```

Accept the default (install all) when it asks which packages from a group.
`libiconv-devel` isn't optional here — the build fails without it (cdrdao's
CD-TEXT handling links against iconv).

### 4. Get cdrdao's source

```
cd /c/dev
git clone https://github.com/cdrdao/cdrdao.git
cd cdrdao
```

### 5. Fix the ntddscsi.h include order

MSYS/Cygwin's `w32api/ntddscsi.h` needs `windows.h` included first (unlike
MinGW-w64's self-contained copy, which is why this only bites in the MSYS
environment). Without this fix, the build fails with a cascade of
type-resolution errors in `dao/ScsiIf-nt.cc`. Apply it once, before building:

```
sed -i '/#include <ntddscsi.h>/d' dao/ScsiIf-nt.cc
sed -i 's|#include <windows.h>|#include <windows.h>\n#include <ntddscsi.h>|' dao/ScsiIf-nt.cc
```

(What this does: removes the original `#include <ntddscsi.h>` line wherever
it is, then adds it back immediately after `#include <windows.h>` — so
`windows.h`'s definitions are visible when `ntddscsi.h` needs them.)

### 6. Build

```
./autogen.sh
./configure
make
```

`make` will take a few minutes. If `./configure` or `make` errors out with
something other than the header-order issue above, paste the exact error —
cdrdao's Windows support is real but less-traveled than its Linux path, so a
small additional fix is plausible.

If it succeeds, `dao/cdrdao.exe` is a real, working Windows binary.

### 7. Build cue2toc (converts a `.cue` to cdrdao's native `.toc`)

```
cd utils
make cue2toc
cd ..
```

### 8. Convert a cue sheet and run

```
./utils/cue2toc -o disc.toc /c/dev/DiscForge/disc.cue
./dao/cdrdao.exe simulate --device D --driver generic-mmc -v 4 disc.toc
```

- `--device D` is just the drive letter (no colon) — cdrdao's Windows
  backend only looks at the first character.
- `simulate` is cdrdao's own non-destructive laser-off test.
- `-v 4` is high verbosity — prints the full cue-sheet table (CTL/ADR, TNO,
  INDEX, DATA FORM, SCMS, MIN, SEC, FRAME) and whether the drive accepted it.
- Note: `--eject-off` is not a real cdrdao flag (an earlier draft of this doc
  had it wrong) — don't pass it; `cdrdao --help` lists the actual option set
  if a different flag is needed.
