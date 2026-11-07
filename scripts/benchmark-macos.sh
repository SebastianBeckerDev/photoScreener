#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
: "${1:?Pass the representative photo folder.}"
mkdir -p "$root/artifacts/performance"
target="$root/artifacts/benchmark-exports/$(uuidgen)"
dotnet run --project "$root/src/PhotoScreener.Desktop" -c Release -- "$1" --export "$target" --benchmark --cache-dir "$root/artifacts/benchmark-cache/$(uuidgen)" --actions 1200 --stable 100 --report "$root/artifacts/performance/macos-window.json"
