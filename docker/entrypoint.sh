#!/usr/bin/env sh
set -eu

PUID="${PUID:-1000}"
PGID="${PGID:-1000}"
UMASK="${UMASK:-002}"

umask "$UMASK"

mkdir -p /config /data /work /trash

if ! getent group "$PGID" >/dev/null 2>&1; then
  groupadd --gid "$PGID" optimisarr
fi

GROUP_NAME="$(getent group "$PGID" | cut -d: -f1)"

if ! getent passwd "$PUID" >/dev/null 2>&1; then
  useradd --uid "$PUID" --gid "$PGID" --home-dir /config --no-create-home --shell /usr/sbin/nologin optimisarr
fi

USER_NAME="$(getent passwd "$PUID" | cut -d: -f1)"

chown -R "$USER_NAME:$GROUP_NAME" /config /work /trash

exec gosu "$USER_NAME:$GROUP_NAME" dotnet /app/Optimisarr.Api.dll
