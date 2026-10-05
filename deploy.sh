#!/usr/bin/env bash
# Builds the plugins and ships them to a remote Valheim server over ssh.
#
#   ./deploy.sh root@1.2.3.4               build, upload, restart, show the startup checks
#   ./deploy.sh --no-build root@1.2.3.4    upload what is already built
#   ./deploy.sh --logs root@1.2.3.4        just follow the remote log
#
# The remote needs no .NET and no Nix: each plugin is a single DLL built here. On a first
# run this also uploads the BepInEx pack, so a bare steamcmd install becomes a modded one.
#
# PLUGINS is the list that gets shipped, in BepInEx/plugins/<name>/<name>.dll order. All
# three go out now: Verse used to be held back because its own send scheduler collided with
# Firehose's, and that scheduler has since been retired. Override to ship fewer:
#   PLUGINS="HallPatton Firehose" ./deploy.sh ...
set -euo pipefail
cd "$(dirname "$0")"

REMOTE_DIR="${REMOTE_DIR:-/opt/valheim}"
SERVICE="${SERVICE:-valheim}"
OWNER="${OWNER:-valheim:valheim}"
PLUGINS="${PLUGINS:-HallPatton Firehose Verse}"
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

if [ "$BUILD" = 1 ]; then
  if [ -z "${VALHEIM_DIR:-}" ]; then exec nix develop --command "$0" "$TARGET"; fi

  # Built against the *dedicated server's* assemblies, not the game client's. They are not
  # the same files - Splatform.dll alone differs by 1.5 KB - and a plugin compiled against
  # one and run against the other resolves some members to the wrong metadata. That is not
  # theoretical: it put a transpiler's own helper beyond reach on the live server, Harmony
  # rejected the rewritten method with an IL compile error, and the plugin went on reporting
  # that it had patched it. Local server install if there is one, client as a last resort.
  SRV="${SERVER_DIR:-$PWD/tools/server}"
  if [ -d "$SRV/valheim_server_Data/Managed" ]; then
    AGAINST=(-p:ValheimDir="$SRV" -p:ValheimManaged="$SRV/valheim_server_Data/Managed")
    echo "building against the server assemblies in $SRV"
  else
    AGAINST=()
    echo "WARNING: no dedicated-server install at $SRV - building against the game client's" >&2
    echo "         assemblies, which are not the ones the server runs." >&2
  fi

  for name in $PLUGINS; do
    dotnet build "src/$name/$name.csproj" -c Release "${AGAINST[@]}" --nologo -v quiet
  done
fi

for name in $PLUGINS; do
  [ -f "src/$name/bin/Release/$name.dll" ] || { echo "no src/$name/bin/Release/$name.dll - build first" >&2; exit 1; }
done

# --- BepInEx, once ------------------------------------------------------------------
if ! ssh "$TARGET" "test -d $REMOTE_DIR/BepInEx/core"; then
  echo "installing BepInEx on the remote..."
  PACK=tools/bepinex/BepInExPack_Valheim
  [ -d "$PACK" ] || { echo "no BepInEx pack at $PACK" >&2; exit 1; }
  tar -C "$PACK" -cz BepInEx doorstop_libs doorstop_config.ini \
    | ssh "$TARGET" "tar -C $REMOTE_DIR -xz"
fi

# --- the plugins ---------------------------------------------------------------------
ssh "$TARGET" "mkdir -p $REMOTE_DIR/BepInEx/config $(for n in $PLUGINS; do printf '%s ' "$REMOTE_DIR/BepInEx/plugins/$n"; done)"
for name in $PLUGINS; do
  DLL="src/$name/bin/Release/$name.dll"
  scp -q "$DLL" "$TARGET:$REMOTE_DIR/BepInEx/plugins/$name/"
  echo "uploaded $name.dll ($(du -h "$DLL" | cut -f1))"
done

# --- the indexes ---------------------------------------------------------------------
# Mark's generated indexes, and only when Mark is part of this deploy: PLUGINS=Firehose is
# meant to change nothing of his. Generated files are replaced; notes.md is the operator's
# and is never overwritten.
case " $PLUGINS " in
*" HallPatton "*)
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
  ;;
*)
  echo "skipped Mark's indexes - HallPatton is not in this deploy"
  ;;
esac

ssh "$TARGET" "chown -R $OWNER $REMOTE_DIR/BepInEx"

# --- restart and report ----------------------------------------------------------------
echo "restarting $SERVICE..."
ssh "$TARGET" "systemctl restart $SERVICE"

echo "waiting for the plugins' startup checks..."
ssh "$TARGET" "
  for i in \$(seq 1 60); do
    if journalctl -u $SERVICE --since '2 min ago' | grep -q 'startup check'; then break; fi
    sleep 5
  done
  journalctl -u $SERVICE --since '3 min ago' | sed -n '/Firehose: startup check/,/reporting /p'
  journalctl -u $SERVICE --since '3 min ago' | sed -n '/Hall-Patton: startup check/,/verbose logging/p'
  journalctl -u $SERVICE --since '3 min ago' | grep -E 'knowledge:|Game server connected' || true
"
