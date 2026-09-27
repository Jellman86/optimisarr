#!/usr/bin/env sh
set -eu
umask "${UMASK:-077}"
config="${OPTIMISARR_CONFIG_DIR:-/config}"
work="${OPTIMISARR_SIDECAR_WORK:-/work}"
mkdir -p "$config" "$work"

if [ "$(id -u)" = 0 ]; then
  uid="${PUID:-1000}"
  gid="${PGID:-1000}"
  getent group "$gid" >/dev/null || groupadd --gid "$gid" optimisarr
  getent passwd "$uid" >/dev/null || useradd --uid "$uid" --gid "$gid" \
    --home-dir "$config" --no-create-home --shell /usr/sbin/nologin optimisarr
  user="$(getent passwd "$uid" | cut -d: -f1)"
  chown "$uid:$gid" "$config" "$work"
  for device in /dev/dri/*; do
    [ -e "$device" ] || continue
    device_gid="$(stat -c '%g' "$device")"
    [ "$device_gid" = 0 ] && continue
    getent group "$device_gid" >/dev/null || groupadd --gid "$device_gid" "dri_$device_gid"
    usermod -aG "$device_gid" "$user"
  done
  exec gosu "$user" dotnet /app/Optimisarr.Sidecar.Linux.dll "$@"
fi
exec dotnet /app/Optimisarr.Sidecar.Linux.dll "$@"
