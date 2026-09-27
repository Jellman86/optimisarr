#!/usr/bin/env bash
# Disposable CI runners only. Managed hosts deploy through their stack manager.
set -euo pipefail
image="${1:?image required}"
result="$(docker run --rm "$image" --discover)"
python3 -c '
import json,sys
c=json.loads(sys.argv[1])
assert c["operatingSystem"] == "linux", c
assert "libx264" in c["videoEncoders"], c
assert c["vmaf"] == 1, c  # A real CPU VMAF comparison, not merely a filter listing.
assert c["freeScratchBytes"] > 0 and c["maxConcurrency"] == 1, c
print("Linux image: real software encode, decode fixture and CPU VMAF probes passed")
' "$result"
# An unpaired image must not claim health or wait forever pretending to work.
if docker run --rm "$image" --healthcheck; then
  echo 'Unpaired image incorrectly reported healthy' >&2
  exit 1
fi
if docker run --rm "$image"; then
  echo 'Worker without pairing unexpectedly succeeded' >&2
  exit 1
fi
