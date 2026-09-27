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
# Headless, with no code and no dashboard, it must stop rather than wait for nothing.
if docker run --rm -e OPTIMISARR_WEB_ENABLED=false "$image"; then
  echo 'Headless worker without pairing unexpectedly succeeded' >&2
  exit 1
fi
# With its dashboard (the image default) it waits on its pairing page, still unhealthy.
container="$(docker run -d --rm -p 127.0.0.1::8788 "$image")"
trap 'docker rm -f "$container" >/dev/null 2>&1 || true' EXIT
port="$(docker port "$container" 8788/tcp | head -n1 | sed 's/.*://')"
offered=''
for _ in $(seq 1 90); do
  status="$(curl -fsS "http://127.0.0.1:$port/api/sidecar/status" 2>/dev/null || true)"
  if python3 -c '
import json,sys
s=json.loads(sys.argv[1])
assert s["state"] == "Unpaired" and s["pairing"]["required"], s
' "$status" 2>/dev/null; then offered=1; break; fi
  sleep 2
done
if [ -z "$offered" ]; then
  echo 'Unpaired dashboard never offered pairing' >&2
  docker logs "$container" >&2 || true
  exit 1
fi
curl -fsS "http://127.0.0.1:$port/" | grep -q 'id="app"' || { echo 'Dashboard page was not served' >&2; exit 1; }
if docker exec "$container" dotnet /app/Optimisarr.Sidecar.Linux.dll --healthcheck; then
  echo 'Worker waiting for pairing incorrectly reported healthy' >&2
  exit 1
fi
echo 'Unpaired image: headless exits, dashboard offers pairing and stays unhealthy'
