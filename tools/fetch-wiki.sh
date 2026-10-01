#!/usr/bin/env bash
# Builds a small factual index from the Valheim wiki and puts it where the plugin can read
# it, so Mark can answer the questions the game files cannot: which biome a thing is found
# in, what summons a boss, what kind of thing something is.
#
# It reads a whitelist of infobox FIELDS - location, biome, type, tameable, and so on - and
# nothing else. No article prose is copied, because none of it is needed: the numbers come
# from the game itself (see Knowledge.cs) and the model writes its own sentences. What lands
# on disk is a short factual index, one line per page.
#
# The wiki is community-written and licensed CC BY-SA 4.0. The index is generated on your
# machine for your server; it is not part of this plugin and is not redistributed with it.
# The source and licence are recorded in the file's own header.
#
#   ./tools/fetch-wiki.sh                 # fetch, resuming if a previous run was cut short
#   FRESH=1 ./tools/fetch-wiki.sh         # ignore previous progress and start over
#   MAX=20 ./tools/fetch-wiki.sh          # just the first 20 pages, to see the shape
#   WIKI_HOST=other.wiki DELAY=2 ./tools/fetch-wiki.sh
#
# It takes a few minutes and the network does not always hold for that long, so every page
# is retried and every page that succeeds is recorded in a .done file beside the output. A
# re-run picks up only what is missing.
set -euo pipefail
cd "$(dirname "$0")/.."

HOST="${WIKI_HOST:-valheim.fandom.com}"
OUT="${OUT:-tools/server/BepInEx/config/HallPatton.wiki.tsv}"
DONE="$OUT.done"
DELAY="${DELAY:-1}"
MAX="${MAX:-0}"
TRIES="${TRIES:-3}"
UA="HallPattonPlugin/2.0 (local Valheim server plugin; one request per page, ${DELAY}s apart)"

if ! command -v jq >/dev/null 2>&1; then exec nix develop --command "$0" "$@"; fi

API="https://$HOST/api.php"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

mkdir -p "$(dirname "$OUT")"

header() {
  {
    echo "# Factual index derived from the $HOST wiki, fetched $(date -u +%Y-%m-%d)."
    echo "# Source: https://$HOST/  -  text there is licensed CC BY-SA 4.0."
    echo "# Only infobox fields are recorded (where a thing is found, what kind it is); no"
    echo "# article prose is copied. Regenerate with tools/fetch-wiki.sh."
    echo "#"
  } > "$OUT"
}

# --- retrying fetch, because a few minutes of requests will meet a blip ---------------
# Writes the page's lead-section wikitext to $TMP/lead.json. Non-zero means every attempt
# failed, which is the one case that must not be recorded as done.
fetch_page() {
  local enc="$1" attempt=1
  while [ "$attempt" -le "$TRIES" ]; do
    if curl -sS -A "$UA" --max-time 40 \
         "$API?action=parse&page=$enc&prop=wikitext&section=0&format=json" \
         > "$TMP/lead.json" 2>"$TMP/err"; then
      return 0
    fi
    sleep $((attempt * 3))
    attempt=$((attempt + 1))
  done
  return 1
}

if [ "${FRESH:-0}" = 1 ] || [ ! -f "$OUT" ] || [ ! -f "$DONE" ]; then
  # No usable progress to resume from. An output file without its .done sidecar is from an
  # older run: keep its lines, and treat the titles in it as done so they are not refetched.
  if [ "${FRESH:-0}" != 1 ] && [ -f "$OUT" ] && [ ! -f "$DONE" ]; then
    cut -f1 < "$OUT" | grep -v '^#' | sort -u > "$DONE"
    echo "no .done file: treating the $(wc -l < "$DONE") titles already in $OUT as fetched"
  else
    header
    : > "$DONE"
  fi
fi

# --- 1. every content page, following the continuation ------------------------------
echo "listing pages on $HOST..."
: > "$TMP/titles"
CONT=""
while :; do
  URL="$API?action=query&list=allpages&apnamespace=0&apfilterredir=nonredirects&aplimit=500&format=json"
  [ -n "$CONT" ] && URL="$URL&apcontinue=$CONT"
  curl -sS -A "$UA" --max-time 40 "$URL" > "$TMP/page.json" || break
  jq -r '.query.allpages[]?.title // empty' "$TMP/page.json" >> "$TMP/titles"
  CONT=$(jq -r '.continue.apcontinue // empty' "$TMP/page.json")
  [ -z "$CONT" ] && break
  CONT=$(jq -rn --arg c "$CONT" '$c|@uri')
  sleep "$DELAY"
done

TOTAL=$(wc -l < "$TMP/titles")
[ "$MAX" -gt 0 ] && head -n "$MAX" "$TMP/titles" > "$TMP/t2" && mv "$TMP/t2" "$TMP/titles"

# Skip what a previous run already got through.
sort -u "$DONE" > "$TMP/done.sorted"
sort -u "$TMP/titles" > "$TMP/titles.sorted"
comm -23 "$TMP/titles.sorted" "$TMP/done.sorted" > "$TMP/todo"
TODO=$(wc -l < "$TMP/todo")
echo "found $TOTAL pages; $((TOTAL - TODO)) already fetched, $TODO to go"
[ "$TODO" -eq 0 ] && { echo "nothing to do - $(grep -vc '^#' "$OUT") entries in $OUT"; exit 0; }

# --- 2. the lead section of each, reduced to whitelisted fields ---------------------
n=0
failed=0
while IFS= read -r title; do
  n=$((n + 1))
  enc=$(jq -rn --arg t "$title" '$t|@uri')

  if ! fetch_page "$enc"; then
    failed=$((failed + 1))
    echo "  ! gave up on \"$title\" after $TRIES tries" >&2
    continue                      # deliberately not recorded as done, so a re-run retries it
  fi

  jq -r '.parse.wikitext["*"] // empty' "$TMP/lead.json" 2>/dev/null \
    | awk -v title="$title" '
      # Wiki markup -> plain text, for one field value.
      function clean(s) {
        gsub(/<br[ ]*\/?>/, ", ", s)
        gsub(/<[^>]*>/, "", s)
        while (match(s, /\{\{[^{}]*\}\}/)) sub(/\{\{[^{}]*\}\}/, "", s)
        gsub(/[{}]/, "", s)                    # a template split over lines leaves a stray brace
        gsub(/\[\[[^]|]*\|/, "[[", s)          # [[target|label]] -> [[label]]
        gsub(/\[\[|\]\]/, "", s)
        gsub(/'"'"''"'"''"'"'|'"'"''"'"'/, "", s)
        gsub(/^[ \t,]+|[ \t,]+$/, "", s)
        gsub(/[ \t]+/, " ", s)
        return s
      }
      # Only these fields, and only their first line - enough to say what a thing is and
      # where it is, which is exactly what the game files do not record.
      /^[ \t]*\|[ \t]*(location|biome|type|tameable|faction|summoned by|summoning items|station|stations|found in)[ \t]*=/ {
        line = $0
        sub(/^[ \t]*\|[ \t]*/, "", line)
        split(line, kv, "=")
        key = kv[1]
        sub(/[ \t]+$/, "", key)
        value = substr(line, index(line, "=") + 1)
        value = clean(value)
        if (value == "" || length(value) > 90) next
        if (tolower(value) == "none" || value == "-") next
        if (!(key in seen)) { seen[key] = 1; order[++count] = key; vals[key] = value }
      }
      END {
        if (count == 0) exit
        out = ""
        for (i = 1; i <= count; i++) {
          k = order[i]
          label = k
          if (k == "location" || k == "biome" || k == "found in") label = "found in"
          if (k == "station" || k == "stations") label = "made at"
          if (k == "summoned by" || k == "summoning items") label = "summoned with"
          out = out (out == "" ? "" : "; ") label " " vals[k]
        }
        printf "%s\t%s: %s\n", title, title, out
      }' >> "$OUT"

  printf '%s\n' "$title" >> "$DONE"
  [ $((n % 25)) -eq 0 ] && echo "  $n/$TODO..."
  sleep "$DELAY"
done < "$TMP/todo"

echo "wrote $(grep -vc '^#' "$OUT") entries to $OUT"
[ "$failed" -gt 0 ] && echo "$failed page(s) failed after $TRIES tries - run again to retry just those"
exit 0
