#!/usr/bin/env bash
# Build and qualify the pinned native image metric in an owned staging prefix.
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
build="${1:?Usage: build_image_quality.sh build-directory install-directory}"
prefix="${2:?Missing install directory}"
cmake -S "$repo/tools/image-quality-native" -B "$build" -DCMAKE_BUILD_TYPE=Release
cmake --build "$build" --config Release --target optimisarr-image-quality --parallel 2
ctest --test-dir "$build" -C Release -R '^native_image_quality$' --output-on-failure
cmake --install "$build" --config Release --component ImageQuality --prefix "$prefix"

if [[ "$(uname -s)" == Darwin ]]; then
  python3 "$(dirname "$0")/check_macos_minimum.py" --minimum 14.0 --system-libraries-only "$prefix/bin/optimisarr-image-quality"
fi
