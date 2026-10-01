#!/usr/bin/env bash
# Sends a Mark-shaped request to the Meta Model API and reports latency, token usage
# and the reply. Used to tune MaxTokens / ReasoningEffort for the plugin.
#
# Reads the key from MODEL_API_KEY so it is never written into this file.
#
#   export MODEL_API_KEY=...
#   ./tools/muse-smoketest.sh                                  # defaults
#   ./tools/muse-smoketest.sh --max-tokens 600 --runs 3
#   ./tools/muse-smoketest.sh --model muse-spark-1.1 --effort low
#   ./tools/muse-smoketest.sh --sweep                           # try a grid and summarise
set -euo pipefail
cd "$(dirname "$0")/.."

if [ -z "${MODEL_API_KEY:-}" ]; then
  echo "MODEL_API_KEY is not set." >&2
  exit 1
fi

MODEL="muse-spark-1.1"
RUNS=1
MAX_TOKENS=1000
EFFORT=""
SWEEP=0

while [ $# -gt 0 ]; do
  case "$1" in
    --model)      MODEL="$2"; shift 2 ;;
    --runs)       RUNS="$2"; shift 2 ;;
    --max-tokens) MAX_TOKENS="$2"; shift 2 ;;
    --effort)     EFFORT="$2"; shift 2 ;;
    --sweep)      SWEEP=1; shift ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

SYS="You are Mark Hall-Patton, museum administrator for the Clark County Museum System in southern Nevada and a well-known historian of the Las Vegas valley, somehow turned up in Valheim. Answer questions about southern Nevada history at your fullest, reaching for names, dates and places. If you are not sure of a fact, say you would want to check it rather than inventing one. Reply in at most 90 words as one paragraph of prose. No emoji, asterisks, stage directions or markdown. A bracketed note about who is asking may precede their words; use it but never read it aloud."
USR="[asked by Bjorn, biome Swamp, night, raining]
Why is Gass Avenue spelled like that?"

# One request. Args: model max_tokens effort. Prints "secs|http|reasoning|completion|finish|content".
one_call() {
  local model="$1" max_tokens="$2" effort="$3"
  jq -n \
    --arg model "$model" \
    --arg sys "$SYS" \
    --arg usr "$USR" \
    --argjson max_tokens "$max_tokens" \
    --arg effort "$effort" \
    '{
       model: $model,
       max_tokens: $max_tokens,
       temperature: 0.7,
       messages: [
         {role: "system",  content: $sys},
         {role: "user",    content: $usr}
       ]
     }
     | if $effort == "" then . else . + {reasoning_effort: $effort} end' > "$TMP/req.json"

  local start end secs code
  start=$(date +%s.%N)
  code=$(curl -sS -o "$TMP/resp.json" -w '%{http_code}' --max-time 60 \
    https://api.meta.ai/v1/chat/completions \
    -H "Authorization: Bearer $MODEL_API_KEY" \
    -H 'Content-Type: application/json' \
    --data @"$TMP/req.json") || code="000"
  end=$(date +%s.%N)
  secs=$(awk -v a="$start" -v b="$end" 'BEGIN{printf "%.2f", b-a}')

  if [ "$code" != "200" ]; then
    local msg
    msg=$(jq -r '.error.message // "(no error message)"' "$TMP/resp.json" 2>/dev/null \
          | sed "s/${MODEL_API_KEY}/<redacted>/g" | head -c 200)
    printf '%s|%s|-|-|-|ERROR: %s\n' "$secs" "$code" "$msg"
    return
  fi

  jq -r --arg secs "$secs" --arg code "$code" '
    [ $secs, $code,
      (.usage.completion_tokens_details.reasoning_tokens // "-" | tostring),
      (.usage.completion_tokens // "-" | tostring),
      (.choices[0].finish_reason // "-"),
      (.choices[0].message.content // "NULL" | gsub("\n"; " "))
    ] | join("|")' "$TMP/resp.json"
}

report() {
  local label="$1" line="$2"
  IFS='|' read -r secs code reasoning completion finish content <<< "$line"
  printf '%-34s %6ss  reasoning=%-4s completion=%-4s finish=%-12s\n' \
    "$label" "$secs" "$reasoning" "$completion" "$finish"
  printf '%-34s %s\n' "" "$content"
}

if [ "$SWEEP" = "1" ]; then
  echo "=== sweep: which settings actually return text? ==="
  for model in muse-spark-1.3 muse-spark-1.1; do
    for mt in 60 200 600; do
      report "$model mt=$mt" "$(one_call "$model" "$mt" "")"
    done
  done
  for eff in low medium; do
    report "muse-spark-1.3 mt=600 eff=$eff" "$(one_call muse-spark-1.3 600 "$eff")"
  done
  exit 0
fi

echo "model=$MODEL max_tokens=$MAX_TOKENS effort=${EFFORT:-default} runs=$RUNS"
for i in $(seq 1 "$RUNS"); do
  report "run $i" "$(one_call "$MODEL" "$MAX_TOKENS" "$EFFORT")"
done
