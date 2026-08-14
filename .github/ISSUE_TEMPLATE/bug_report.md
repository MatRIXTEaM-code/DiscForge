---
name: Bug report
about: Something behaves incorrectly, crashes, or declines when it shouldn't
title: ''
labels: bug
assignees: ''
---

**What happened**
The command or GUI action, and what went wrong.

**Exact command / action**
```
dforge <command> <args>        # add --json where the command supports it
```

**Full output**
```
(paste it all — the last line usually matters most)
```

**Environment**
- DiscForge version (release tag or commit):
- OS:
- For drive operations: drive vendor/model (from `dforge drives`):

**The input file (for format bugs)**
Do **NOT** attach copyrighted disc images. Instead attach/paste:
- `dforge identify <file>` output, and
- the relevant `*-info` command output on it, and/or
- a small synthetic file that reproduces the issue (see `docs/reference/` for
  the fixture generators).

**Expected behaviour**
What you believe should have happened — if you're citing a format spec, a link
helps.
