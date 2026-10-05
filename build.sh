#!/usr/bin/env bash
# Builds the plugins and copies them into the server's BepInEx/plugins folder.
# Run inside the devshell (`nix develop`) or let this script enter it for you.
set -euo pipefail
cd "$(dirname "$0")"

if [ -z "${VALHEIM_DIR:-}" ]; then
  exec nix develop --command ./build.sh "$@"
fi

for proj in src/HallPatton/HallPatton.csproj src/Verse/Verse.csproj src/Firehose/Firehose.csproj; do
  dotnet build "$proj" -c Release "$@"
done
