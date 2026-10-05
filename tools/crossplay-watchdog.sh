#!/usr/bin/env bash
# Backs the live server out of -crossplay if its PlayFab session never activates.
#
# Why this exists, from HANDOFF.md: crossplay failed four times running by getting stuck in
# PlayFab's `State.Creating`. That failure is silent and total - systemd reports the service
# active, all three plugins print clean startup checks, and the only evidence is the *absence*
# of one log line. Meanwhile every discovery path (join code, name search, FindServerByIp)
# filters on a lobby field that only `ActivateSession` sets, so nobody can join at all. There is
# no timeout in the game for it.
#
# So: wait for `Session ... is active with` after a crossplay start, and if it does not come,
# comment CROSSPLAY out of the env file and restart. The Steam backend is discoverable by
# address and was the working configuration, so backing out is strictly better than sitting
# there unreachable.
#
# Installed as its own root-owned oneshot unit rather than an ExecStartPost, because the server
# unit runs as User=valheim with ProtectSystem=full and so cannot edit /etc or restart itself.
# It is WantedBy=valheim.service, so it runs on every start - and on the start that follows its
# own revert it sees no -crossplay and exits immediately, which is why it cannot loop.
set -uo pipefail

SERVICE="${SERVICE:-valheim}"
ENVFILE="${ENVFILE:-/etc/valheim.env}"

# Long enough for a PlayFab login, a Party network join and a join-code check on a cold boot;
# the working run did all three inside 20 s.
WAIT="${WAIT:-300}"

say() { logger -t valheim-crossplay -- "$*"; echo "crossplay-watchdog: $*"; }

pid="$(systemctl show -p MainPID --value "$SERVICE" 2>/dev/null)"
[ -n "$pid" ] && [ "$pid" != "0" ] || { say "no main pid for $SERVICE; nothing to watch"; exit 0; }

# Only a crossplay start is watched. /proc/.../cmdline is NUL-separated.
if ! tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null | grep -q -- '-crossplay'; then
  exit 0
fi

say "watching pid $pid for a PlayFab session, up to ${WAIT}s"

deadline=$((SECONDS + WAIT))
while [ "$SECONDS" -lt "$deadline" ]; do
  if journalctl -u "$SERVICE" _PID="$pid" --no-pager 2>/dev/null | grep -q 'is active with'; then
    say "the session activated - crossplay is up and discoverable"
    exit 0
  fi

  # The process going away is a crossplay failure too, and a worse-shaped one: a SERVER_NAME
  # over 54 characters fails the PlayFab login and calls Application.Quit(), which Restart=always
  # then retries for ever. Backing out breaks that loop.
  if ! kill -0 "$pid" 2>/dev/null; then
    say "the server exited while starting in crossplay - backing out"
    break
  fi

  sleep 10
done

if journalctl -u "$SERVICE" _PID="$pid" --no-pager 2>/dev/null | grep -q 'is active with'; then
  say "the session activated just in time - leaving it alone"
  exit 0
fi

say "no 'is active with' line from pid $pid - backing out to the Steam backend"
sed -i 's/^CROSSPLAY=/#CROSSPLAY=/' "$ENVFILE"
systemctl --no-block restart "$SERVICE"
