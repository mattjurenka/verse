#!/usr/bin/env bash
# Builds the plugin and copies it into the server's BepInEx/plugins folder.
# Run inside the devshell (`nix develop`) or let this script enter it for you.
set -euo pipefail
cd "$(dirname "$0")"

if [ -z "${VALHEIM_DIR:-}" ]; then
  exec nix develop --command ./build.sh "$@"
fi

dotnet build src/HallPatton/HallPatton.csproj -c Release "$@"
