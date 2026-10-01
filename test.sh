#!/usr/bin/env bash
# Runs the tests against the real Unity-free source files in src/HallPatton.
set -euo pipefail
cd "$(dirname "$0")"
if [ -z "${VALHEIM_DIR:-}" ]; then exec nix develop --command ./test.sh "$@"; fi
dotnet run --project tests/TextTests/TextTests.csproj "$@"
