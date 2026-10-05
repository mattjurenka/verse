#!/usr/bin/env bash
# Ships this directory's panel to the droplet and restarts it.
#
#   ./deploy.sh                      to the usual host
#   ./deploy.sh root@other-host      somewhere else
#
# This does not touch the game. `valpanel` is a different unit running as a different user
# with no connection to the Valheim server, so players will not notice the restart - which is
# the whole reason approving a passkey is allowed to restart it.
#
# Only the panel's own files are sent. The venv on the box is left alone; if requirements.txt
# has changed, update it there by hand:
#   /opt/valpanel/venv/bin/pip install -r /opt/valpanel/requirements.txt
set -euo pipefail
cd "$(dirname "$0")"

TARGET="${1:-root@valheim.jurenka.software}"
REMOTE="${REMOTE:-/opt/valpanel}"

for f in app.py metrics.py visitors.py static/index.html static/app.js static/app.css; do
  [ -f "$f" ] || { echo "missing $f" >&2; exit 1; }
done

echo "sending app.py, metrics.py, visitors.py and static/ to $TARGET:$REMOTE ..."
tar -cz app.py metrics.py visitors.py static | ssh "$TARGET" "tar -C $REMOTE -xz"

echo "restarting valpanel ..."
ssh "$TARGET" "systemctl restart valpanel"
sleep 2
ssh "$TARGET" "systemctl is-active valpanel && journalctl -u valpanel -n 5 --no-pager"
