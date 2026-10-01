#!/usr/bin/env bash
# Runs a local Valheim dedicated server with this plugin installed, so you can connect from
# the game and try it. Installs BepInEx into the server folder and builds/deploys the plugin
# first, both idempotently.
#
#   ./server.sh              start it (foreground, Ctrl-C to stop)
#   ./server.sh --logs       follow the plugin's log instead of starting anything
#   ./server.sh --clean      delete the test world, then start fresh
#
# Override anything: WORLD=Foo PORT=2456 PASSWORD=secret NAME="My server" ./server.sh
set -euo pipefail
cd "$(dirname "$0")"
REPO="$PWD"

SERVER_DIR="${SERVER_DIR:-$REPO/tools/server}"
SAVE_DIR="${SAVE_DIR:-$SERVER_DIR/save}"
WORLD="${WORLD:-PattonTest}"
PORT="${PORT:-2456}"
PASSWORD="${PASSWORD:-museum}"
NAME="${NAME:-Hall-Patton test}"
LOG="$SERVER_DIR/BepInEx/LogOutput.log"

if [ "${1:-}" = "--logs" ]; then
  [ -f "$LOG" ] || { echo "No log yet at $LOG - start the server first."; exit 1; }
  exec tail -n 200 -f "$LOG"
fi

if [ "${1:-}" = "--clean" ]; then
  rm -rf "$SAVE_DIR"
  echo "removed $SAVE_DIR - the world will be regenerated"
fi

# --- the server itself ---------------------------------------------------------------
if [ ! -x "$SERVER_DIR/valheim_server.x86_64" ]; then
  cat <<MSG
No dedicated server in $SERVER_DIR.

Install it with steamcmd - it is app 896660, anonymous login, no account needed (~2.1 GB):

  NIXPKGS_ALLOW_UNFREE=1 nix run --impure nixpkgs#steamcmd -- \\
    +force_install_dir "$SERVER_DIR" +login anonymous +app_update 896660 validate +quit

MSG
  exit 1
fi

# --- BepInEx ------------------------------------------------------------------------
if [ ! -d "$SERVER_DIR/BepInEx/core" ]; then
  PACK="$REPO/tools/bepinex/BepInExPack_Valheim"
  if [ ! -d "$PACK" ]; then
    echo "No BepInEx pack at $PACK - get BepInExPack_Valheim and unzip it there." >&2
    exit 1
  fi
  cp -r "$PACK/BepInEx" "$PACK/doorstop_libs" "$PACK/doorstop_config.ini" "$SERVER_DIR/"
  echo "installed BepInEx into $SERVER_DIR"
fi

# --- the plugin ---------------------------------------------------------------------
# Built against the *server's* own assemblies, so this works on a box with no game client.
if [ -z "${VALHEIM_DIR:-}" ]; then
  exec nix develop --command "$0" "$@"
fi

dotnet build src/HallPatton/HallPatton.csproj -c Release \
  -p:ValheimDir="$SERVER_DIR" \
  -p:ValheimManaged="$SERVER_DIR/valheim_server_Data/Managed" \
  --nologo -v quiet
echo "deployed the plugin into $SERVER_DIR/BepInEx/plugins/HallPatton"

if [ -z "${MODEL_API_KEY:-}" ]; then
  echo "note: MODEL_API_KEY is not set, so Muse is off and the built-in lines will answer."
  echo "      export MODEL_API_KEY=... (or set Muse.ApiKey in BepInEx/config) to enable it."
fi

mkdir -p "$SAVE_DIR"

cat <<MSG

--------------------------------------------------------------------
  Server : $NAME
  World  : $WORLD   (save dir: $SAVE_DIR)
  Join   : Start Valheim -> character -> Join Game -> Join IP
           127.0.0.1:$PORT      password: $PASSWORD
  Then   : type  !mark  in chat. '!mark debug' reports what the
           server heard and where the reply went.
  Log    : ./server.sh --logs
--------------------------------------------------------------------
  Startup takes up to a minute the first time (world generation).
  Wait for "Game server connected" before you try to join.
--------------------------------------------------------------------

MSG

# --- run ----------------------------------------------------------------------------
cd "$SERVER_DIR"

# The doorstop variables are the ones from the BepInEx pack's own start_server_bepinex.sh.
# LD_PRELOAD is given as an absolute path rather than the pack's bare filename, because the
# loader that ends up resolving it may not have our LD_LIBRARY_PATH - see the cases below.
export DOORSTOP_ENABLED=1
export DOORSTOP_TARGET_ASSEMBLY=./BepInEx/core/BepInEx.Preloader.dll
export LD_PRELOAD="$SERVER_DIR/doorstop_libs/libdoorstop_x64.so:${LD_PRELOAD:-}"
export LD_LIBRARY_PATH="$SERVER_DIR/doorstop_libs:$SERVER_DIR/linux64:${LD_LIBRARY_PATH:-}"
export SteamAppId=892970

ARGS=(-name "$NAME" -port "$PORT" -world "$WORLD" -password "$PASSWORD"
      -savedir "$SAVE_DIR" -public 0)

# This is a prebuilt Unity binary, so it needs a filesystem laid out the way it expects.
if [ -n "${NIX_LD:-}" ]; then
  # nix-ld is the cheap path: it makes an unpatched ELF just run, and unlike an FHS
  # sandbox it survives being started from a non-interactive or already-sandboxed
  # context. The server needs nothing beyond nix-ld's default library set plus the
  # linux64/ it ships itself.
  export NIX_LD_LIBRARY_PATH="$SERVER_DIR/linux64:${NIX_LD_LIBRARY_PATH:-}"
  exec ./valheim_server.x86_64 "${ARGS[@]}"
elif [ -e /etc/NIXOS ] || [ "${USE_FHS:-0}" = 1 ]; then
  # NixOS without nix-ld: wrap it in the FHS environment from the flake.
  exec nix run "$REPO#fhs" -- ./valheim_server.x86_64 "${ARGS[@]}"
else
  # An ordinary distribution already looks the way the binary expects.
  exec ./valheim_server.x86_64 "${ARGS[@]}"
fi
