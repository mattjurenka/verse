#!/usr/bin/env bash
# One-shot runbook for turning the server's password off on the live box.
#
#   ./tools/deploy-nopassword.sh root@valheim.jurenka.software
#   ./tools/deploy-nopassword.sh --verify-only root@valheim.jurenka.software
#
# Three phases, in this order on purpose:
#
#   1. Remote prep, no restart. Seeds the new NoPassword config key and renames the server.
#      The key has to exist in com.matthew.verse.cfg *before* the new DLL starts, or BepInEx
#      writes it at its false default on the first run and the password stays on until a
#      second restart. SERVER_NAME moves from "Any Password" to "No Password", which is true
#      again now and one byte shorter.
#   2. ./deploy.sh, which builds, uploads all three plugins and restarts the service.
#   3. Verification, from the journal and off the wire. The service sometimes dies on the
#      *first* restart after a deploy with a transient Harmony/Mono error and comes back
#      clean within a couple of seconds, so this reads the real current MainPID rather than
#      trusting a log tail.
#
# Both edited files are backed up in place as *.bak-<timestamp> first, the pattern the rest
# of this repo's server work uses. Re-running is safe: the config seed and the rename are
# both idempotent.
set -euo pipefail
cd "$(dirname "$0")/.."

VERIFY_ONLY=0
while [ $# -gt 0 ]; do
  case "$1" in
    --verify-only) VERIFY_ONLY=1; shift ;;
    *) TARGET="$1"; shift ;;
  esac
done
[ -n "${TARGET:-}" ] || { echo "usage: $0 [--verify-only] user@host" >&2; exit 2; }

CFG=/opt/valheim/BepInEx/config/com.matthew.verse.cfg
ENVF=/etc/valheim.env
# 43 bytes, against Steam's 63. Also under the 54-byte PlayFab account-id ceiling, so
# re-enabling -crossplay still needs no name change - that was a deliberate choice when the
# name was cut to 44, and this keeps it.
NEWNAME='_<color=#FFFF00>VERSE</color> - No Password'

if [ "$VERIFY_ONLY" = 0 ]; then
  echo "=== 1. remote prep (no restart) ==============================================="

  # Quoted heredoc: everything below runs on the remote, and nothing is expanded here
  # except the two values exported into the environment first.
  ssh "$TARGET" "CFG='$CFG' ENVF='$ENVF' NEWNAME='$NEWNAME' bash -euo pipefail" <<'REMOTE'
TS=$(date +%Y%m%d-%H%M%S)

# --- the plugin config: seed NoPassword = true --------------------------------------
# Inserted directly after AcceptAnyPassword so it lands inside the same [Verse] section;
# appending to the end of the file would put it under whichever section happens to be last.
if [ ! -f "$CFG" ]; then
  echo "  !! $CFG not found - has the plugin ever run on this box?" >&2
  exit 1
fi

if grep -qE '^[[:space:]]*NoPassword[[:space:]]*=' "$CFG"; then
  sed -i.bak-"$TS" -E 's/^[[:space:]]*NoPassword[[:space:]]*=.*/NoPassword = true/' "$CFG"
  echo "  config: NoPassword already present, set to true (backup: $CFG.bak-$TS)"
else
  cp "$CFG" "$CFG.bak-$TS"
  awk '
    { print }
    /^[[:space:]]*AcceptAnyPassword[[:space:]]*=/ && !done {
      print ""
      print "NoPassword = true"
      done = 1
    }
  ' "$CFG.bak-$TS" > "$CFG.new"

  if ! grep -qx 'NoPassword = true' "$CFG.new"; then
    echo "  !! could not find AcceptAnyPassword in $CFG to anchor to; left it alone" >&2
    rm -f "$CFG.new"
    exit 1
  fi
  # Match the ownership BepInEx writes with, or the game cannot rewrite the file later.
  chown --reference="$CFG" "$CFG.new"
  chmod --reference="$CFG" "$CFG.new"
  mv "$CFG.new" "$CFG"
  echo "  config: seeded NoPassword = true (backup: $CFG.bak-$TS)"
fi

grep -E '^(NoPassword|AcceptAnyPassword)[[:space:]]*=' "$CFG" | sed 's/^/    /'

# --- the server name ------------------------------------------------------------------
# Only the SERVER_NAME line is touched. PASSWORD stays exactly as it is: it is still
# required on ExecStart, because FejdStartup.ParseServerArguments validates the command
# line and calls Application.Quit() on a listed server without a valid one, well before
# ZNet.SetServer runs. Removing it would stop the server booting at all.
if [ ! -f "$ENVF" ]; then
  echo "  !! $ENVF not found" >&2
  exit 1
fi

cp "$ENVF" "$ENVF.bak-$TS"
if grep -q '^SERVER_NAME=' "$ENVF"; then
  # The replacement is passed through an awk variable rather than a sed replacement, so
  # the name's own & and / characters cannot be reinterpreted.
  awk -v new="$NEWNAME" '
    /^SERVER_NAME=/ { print "SERVER_NAME=\"" new "\""; next }
    { print }
  ' "$ENVF.bak-$TS" > "$ENVF.new"
  chown --reference="$ENVF" "$ENVF.new"
  chmod --reference="$ENVF" "$ENVF.new"
  mv "$ENVF.new" "$ENVF"
  echo "  env: SERVER_NAME updated (backup: $ENVF.bak-$TS)"
else
  echo "  !! no SERVER_NAME line in $ENVF; left it alone" >&2
  exit 1
fi

# Printed back deliberately narrowly - a plain cat of this file also shows PASSWORD.
grep '^SERVER_NAME=' "$ENVF" | sed 's/^/    /'
printf '    %d bytes (Steam allows 63)\n' \
  "$(grep '^SERVER_NAME=' "$ENVF" | sed 's/^SERVER_NAME=//; s/^"//; s/"$//' | tr -d '\n' | wc -c)"
REMOTE

  echo
  echo "=== 2. build, upload, restart =================================================="
  # No daemon-reload needed: the unit reads SERVER_NAME through EnvironmentFile, which
  # systemd re-reads on every start. Only a change to the unit file itself would want one.
  ./deploy.sh "$TARGET"
fi

echo
echo "=== 3. verification ============================================================"
ssh "$TARGET" 'bash -euo pipefail' <<'VERIFY'
echo "--- service ---"
systemctl show valheim -p MainPID,ActiveEnterTimestamp,NRestarts | sed 's/^/  /'
PID=$(systemctl show valheim -p MainPID --value)

echo
echo "--- the startup check, and the removal itself ---"
# The startup check prints from Awake, which runs BEFORE ZNet.SetServer, so it can only
# report intent ("password being removed"). The postfix's own line is the confirmation,
# and the "no padlock ... no dialog" wording means it found a populated field to clear.
journalctl -u valheim --since '5 min ago' \
  | grep -E 'Verse [0-9.]+ startup check|password (being )?removed|STILL ASKED FOR|did not apply|Harmony rejected' \
  | tail -10 | sed 's/^/  /' || echo "  (nothing matched - widen the --since window)"

echo
echo "--- registration ---"
journalticket=""
journalctl -u valheim --since '5 min ago' \
  | grep -E 'Opened Steam server|Registering lobby|Game server connected|register server failed' \
  | tail -5 | sed 's/^/  /' || true

echo
echo "--- anything unhappy ---"
journalctl -u valheim --since '5 min ago' -p warning \
  | grep -vE 'Shutting down|^--' | tail -10 | sed 's/^/  /' || echo "  (clean)"
VERIFY

echo
echo "--- off the wire (A2S on 2457) -------------------------------------------------"
# The decisive external check. The I-response fields run: protocol, name, map, folder,
# game, appid, players, max players, bots, type (d), environment (l), visibility. Three
# things to read out of it:
#   * the name, whole, with the colour tag intact and not silently cut at 63 bytes
#   * max players 25, not 10
#   * visibility 0, NOT 1 - that is the padlock going away, which is what proves the
#     blanking reached ZNet.OpenServer and not just the journal
HOST=${TARGET#*@}
if command -v socat >/dev/null 2>&1; then
  CH=$(printf '\xff\xff\xff\xffTSource Engine Query\x00' \
        | socat -T4 - "UDP:$HOST:2457" 2>/dev/null | tail -c 4 | od -An -tx1 | tr -d ' \n') || true
  if [ -n "${CH:-}" ]; then
    printf "\xff\xff\xff\xffTSource Engine Query\x00\x${CH:0:2}\x${CH:2:2}\x${CH:4:2}\x${CH:6:2}" \
      | socat -T4 - "UDP:$HOST:2457" | od -A d -c | head -20
    echo
    echo "  read 'visibility' near the end: 0 = no padlock (what we want), 1 = still locked."
  else
    echo "  no challenge came back - retry, the query socket is occasionally slow to answer."
  fi
else
  echo "  socat not installed locally; run inside 'nix develop' or check from the box."
fi

echo
echo "Still only checkable from a Valheim client: that the join now raises no password"
echo "dialog at all. The mechanism was proven against a vanilla client on 2026-10-02;"
echo "what this deploy changes is the config gating, not the trick."
