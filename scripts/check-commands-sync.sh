#!/usr/bin/env bash
# check-commands-sync.sh — fail if any dforge command is missing from docs/COMMANDS.md.
# Runs `dforge` (no args), extracts the command tokens from its help, and confirms each
# appears in docs/COMMANDS.md. Wire this into CI so help and the reference cannot drift.
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
dll="$(find "$repo/src/DiscForge.Cli" -path '*/bin/*/net8.0/dforge.dll' 2>/dev/null | head -1)"
if [ -z "$dll" ]; then echo "Build the CLI first (dotnet build -f net8.0)."; exit 2; fi

help_cmds="$(dotnet "$dll" 2>/dev/null | grep -oE '^  [a-z][a-z0-9-]+' | grep -oE '[a-z0-9-]+' | sort -u)"
doc_cmds="$(grep -oE '^- `[a-z][a-z0-9-]+' "$repo/docs/COMMANDS.md" | grep -oE '[a-z0-9-]+$' | sort -u)"

missing="$(comm -23 <(echo "$help_cmds") <(echo "$doc_cmds"))"
if [ -n "$missing" ]; then
  echo "COMMANDS.md is missing these commands shown in help:"; echo "$missing" | sed 's/^/  - /'
  echo "Regenerate docs/COMMANDS.md from the CLI help."; exit 1
fi
echo "OK: every dforge command is documented in COMMANDS.md ($(echo "$help_cmds" | wc -l | tr -d ' ') commands)."
