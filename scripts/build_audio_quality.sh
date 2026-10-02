#!/usr/bin/env bash
# Build the pinned metric and its licences into an owned staging prefix.
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
build="${1:?Usage: build_audio_quality.sh build-directory install-directory}"
prefix="${2:?Missing install directory}"
cmake -S "$repo/tools/audio-quality-native" -B "$build" -DCMAKE_BUILD_TYPE=Release
cmake --build "$build" --config Release --parallel 2
ctest --test-dir "$build" -C Release --output-on-failure
cmake --install "$build" --config Release --prefix "$prefix"
