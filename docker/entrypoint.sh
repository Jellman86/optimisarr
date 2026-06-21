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

# Grant the app user access to DRI render nodes so ffmpeg can use hardware encoders
# (QSV, VA-API). Docker's --device passthrough maps the host render group GID into the
# container, but gosu user:group drops all supplementary groups, so we must add the GID
# to the user explicitly before switching context. We iterate all devices under /dev/dri
# so both the primary render node and any additional cards are covered.
if [ -d /dev/dri ]; then
  for DEV in /dev/dri/*; do
    [ -e "$DEV" ] || continue
    DEV_GID="$(stat -c '%g' "$DEV" 2>/dev/null)" || continue
    [ "$DEV_GID" = "0" ] && continue
    if ! getent group "$DEV_GID" >/dev/null 2>&1; then
      groupadd --gid "$DEV_GID" "dri_${DEV_GID}"
    fi
    usermod -aG "$DEV_GID" "$USER_NAME" 2>/dev/null || true
  done
fi

# Use just the username (not user:group) so gosu preserves the supplementary groups
# we just added. The primary group is still $GROUP_NAME from the passwd entry.
exec gosu "$USER_NAME" dotnet /app/Optimisarr.Api.dll
