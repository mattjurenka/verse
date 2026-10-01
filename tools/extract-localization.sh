#!/usr/bin/env bash
# Pulls the English localization table out of the game's resources.assets into a TSV the
# plugin can read, so Mark can call things by the names players see rather than by prefab
# name, and can read an item's own description off it like a museum label.
#
# The game has a Localization class for this, but it is client-only: it is not in the
# dedicated server's assembly_valheim.dll at all. The strings themselves are in the server's
# own resources.assets, which is what this reads.
#
#   ./tools/extract-localization.sh [resources.assets] [output.tsv]
set -euo pipefail
cd "$(dirname "$0")/.."

SRC="${1:-tools/server/valheim_server_Data/resources.assets}"
OUT="${2:-tools/server/BepInEx/config/HallPatton.localization.tsv}"

if [ ! -f "$SRC" ]; then
  echo "No resources.assets at $SRC" >&2
  exit 1
fi

if ! command -v strings >/dev/null 2>&1; then exec nix develop --command "$0" "$@"; fi

mkdir -p "$(dirname "$OUT")"

# Rows are CSV: "token","English","Swedish",...  English is ASCII, so it survives `strings`
# even where later columns do not. Tabs and stray control characters are stripped so the
# output stays one record per line.
strings -n 6 "$SRC" \
  | grep -oE '^"[a-zA-Z0-9_]+","[^"]*"' \
  | sed -E 's/^"([a-zA-Z0-9_]+)","(.*)"$/\1\t\2/' \
  | tr -d '\r' \
  | awk -F'\t' '!seen[$1]++ && NF==2 && length($2) > 0' \
  > "$OUT"

echo "wrote $(wc -l < "$OUT") entries to $OUT"
