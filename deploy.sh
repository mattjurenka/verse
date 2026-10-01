#!/usr/bin/env bash
# Builds the plugin and ships it to a remote Valheim server over ssh.
#
#   ./deploy.sh root@1.2.3.4               build, upload, restart, show the startup check
#   ./deploy.sh --no-build root@1.2.3.4    upload what is already built
#   ./deploy.sh --logs root@1.2.3.4        just follow the remote log
#
# The remote needs no .NET and no Nix: the plugin is a single DLL built here. On a first
# run this also uploads the BepInEx pack, so a bare steamcmd install becomes a modded one.
set -euo pipefail
cd "$(dirname "$0")"

REMOTE_DIR="${REMOTE_DIR:-/opt/valheim}"
SERVICE="${SERVICE:-valheim}"
OWNER="${OWNER:-valheim:valheim}"
BUILD=1

while [ $# -gt 0 ]; do
  case "$1" in
    --no-build) BUILD=0; shift ;;
    --logs) shift; exec ssh "$1" "journalctl -u $SERVICE -n 200 -f" ;;
    *) TARGET="$1"; shift ;;
  esac
done

if [ -z "${TARGET:-}" ]; then
  echo "usage: ./deploy.sh [--no-build|--logs] user@host" >&2
  exit 2
fi

DLL=src/HallPatton/bin/Release/HallPatton.dll

if [ "$BUILD" = 1 ]; then
  if [ -z "${VALHEIM_DIR:-}" ]; then exec nix develop --command "$0" ${BUILD:+} "$TARGET"; fi
  dotnet build src/HallPatton/HallPatton.csproj -c Release --nologo -v quiet
fi

[ -f "$DLL" ] || { echo "no $DLL - build first" >&2; exit 1; }

# --- BepInEx, once ------------------------------------------------------------------
if ! ssh "$TARGET" "test -d $REMOTE_DIR/BepInEx/core"; then
  echo "installing BepInEx on the remote..."
  PACK=tools/bepinex/BepInExPack_Valheim
  [ -d "$PACK" ] || { echo "no BepInEx pack at $PACK" >&2; exit 1; }
  tar -C "$PACK" -cz BepInEx doorstop_libs doorstop_config.ini \
    | ssh "$TARGET" "tar -C $REMOTE_DIR -xz"
fi

# --- the plugin ---------------------------------------------------------------------
ssh "$TARGET" "mkdir -p $REMOTE_DIR/BepInEx/plugins/HallPatton $REMOTE_DIR/BepInEx/config"
scp -q "$DLL" "$TARGET:$REMOTE_DIR/BepInEx/plugins/HallPatton/"
echo "uploaded $(basename "$DLL") ($(du -h "$DLL" | cut -f1))"

# --- the indexes ---------------------------------------------------------------------
# Generated files are replaced; notes.md is the operator's and is never overwritten.
for f in HallPatton.localization.tsv HallPatton.wiki.tsv; do
  LOCAL="tools/server/BepInEx/config/$f"
  [ -f "$LOCAL" ] || continue
  scp -q "$LOCAL" "$TARGET:$REMOTE_DIR/BepInEx/config/"
  echo "uploaded $f ($(grep -vc '^#' "$LOCAL") entries)"
done

LOCAL_NOTES=tools/server/BepInEx/config/HallPatton.notes.md
if [ -f "$LOCAL_NOTES" ] && ! ssh "$TARGET" "test -f $REMOTE_DIR/BepInEx/config/HallPatton.notes.md"; then
  scp -q "$LOCAL_NOTES" "$TARGET:$REMOTE_DIR/BepInEx/config/"
  echo "uploaded notes.md (first time only - yours from now on)"
fi

ssh "$TARGET" "chown -R $OWNER $REMOTE_DIR/BepInEx"

# --- restart and report ----------------------------------------------------------------
echo "restarting $SERVICE..."
ssh "$TARGET" "systemctl restart $SERVICE"

echo "waiting for the plugin's startup check..."
ssh "$TARGET" "
  for i in \$(seq 1 60); do
    if journalctl -u $SERVICE --since '2 min ago' | grep -q 'startup check'; then break; fi
    sleep 5
  done
  journalctl -u $SERVICE --since '3 min ago' \
    | sed -n '/startup check/,/verbose logging/p'
  journalctl -u $SERVICE --since '3 min ago' | grep -E 'knowledge:|Game server connected' || true
"
