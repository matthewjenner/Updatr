#!/usr/bin/env bash
#
# Clean, rebuild, and run Updatr.
# Usage:  ./Scripts/run.sh [Debug|Release] [App|Cli] [-- cli args...]
#
#   ./Scripts/run.sh                   # GUI, Debug
#   ./Scripts/run.sh Release           # GUI, Release
#   ./Scripts/run.sh Debug Cli -- --help
#
set -euo pipefail

CONFIGURATION="${1:-Debug}"
if [[ "$CONFIGURATION" != "Debug" && "$CONFIGURATION" != "Release" ]]; then
    echo "error: configuration must be 'Debug' or 'Release', got '$CONFIGURATION'" >&2
    exit 1
fi

TARGET="${2:-App}"
if [[ "$TARGET" != "App" && "$TARGET" != "Cli" ]]; then
    echo "error: target must be 'App' or 'Cli', got '$TARGET'" >&2
    exit 1
fi

shift $(( $# < 2 ? $# : 2 ))
[[ "${1:-}" == "--" ]] && shift

# Run from the repo root regardless of where the script was invoked.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.."

step() { printf '\n==> %s\n' "$1"; }

step "Cleaning"
dotnet clean --configuration "$CONFIGURATION" --nologo -v q

step "Building"
dotnet build --configuration "$CONFIGURATION" --nologo

step "Running Updatr.$TARGET  (Ctrl+C to stop)"
# --no-build: run exactly what was just built, with no implicit rebuild.
dotnet run --project "Src/Updatr.$TARGET" --configuration "$CONFIGURATION" --no-build -- "$@"
