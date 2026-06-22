#!/usr/bin/env bash
set -euo pipefail

image="${1:?usage: ci_container_smoke.sh IMAGE}"
name="optimisarr-ci-smoke-$$"
root="$(mktemp -d)"
cleanup() {
  docker rm -f "$name" >/dev/null 2>&1 || true
  rm -rf "$root"
}
trap cleanup EXIT

mkdir -p "$root"/{config,data,work,trash}
docker run -d --name "$name" -p 127.0.0.1::8787 \
  -e PUID="$(id -u)" -e PGID="$(id -g)" \
  -v "$root/config:/config" -v "$root/data:/data" -v "$root/work:/work" -v "$root/trash:/trash" \
  "$image" >/dev/null

for _ in {1..30}; do
  port="$(docker port "$name" 8787/tcp | awk -F: '{print $NF}')"
  if curl --fail --silent "http://127.0.0.1:$port/api/ready" >/dev/null; then
    exit 0
  fi
  sleep 1
done

docker logs "$name"
echo "Optimisarr container did not become ready" >&2
exit 1
