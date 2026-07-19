# Optimisarr on Unraid

Optimisarr runs as a single Docker container. This guide covers installing it on Unraid, the volume
layout that keeps replacements safe, and enabling hardware transcoding.

## Install

Until Optimisarr is listed in Community Applications, add the template by URL:

1. **Docker → Add Container → Template ▾ →** paste
   `https://raw.githubusercontent.com/Jellman86/optimisarr/main/unraid/optimisarr.xml`.
2. Set the paths below and click **Apply**.
3. Open the WebUI at `http://<server>:8787/`.

The image is `ghcr.io/jellman86/optimisarr:latest`. It exposes port **8787** and reports health at
`/api/ready`.

## Volumes

| Container path | What it holds | Notes |
|----------------|---------------|-------|
| `/config` | SQLite database + configuration | Keep on fast storage (e.g. `appdata`). |
| `/data` | Storage root: media, work, and quarantine | **Read-write.** Add libraries from `/data`; the template keeps work and quarantine under `/data/.optimisarr`. |

**Why one mapping matters.** After a converted file passes verification, Optimisarr moves the
original into quarantine (recoverable by rollback) and moves the verified output into place.
Keeping media, work, and quarantine below the single `/data` container mapping lets those be atomic
moves. Separate container bind mounts are distinct move boundaries even when their host paths are
on the same filesystem, so Optimisarr must use its slower verified copy-plus-delete fallback.

The template sets `OPTIMISARR_WORK_DIR=/data/.optimisarr/work` and
`OPTIMISARR_TRASH_DIR=/data/.optimisarr/trash`. You can move work to fast scratch storage, but doing
so intentionally gives up atomic work-to-library moves; keep the verified cross-filesystem fallback
enabled and use the setup Re-test action to confirm the effective relationship.

No original is ever deleted or overwritten until a verified replacement exists — a failed or
re-eligible job leaves the source untouched.

## Access control

Set **Admin Token** (`OPTIMISARR_ADMIN_TOKEN`) to require a bearer token for the API and UI, or leave
it blank and place Optimisarr behind an authenticated reverse proxy for remote access. Leaving it
blank on a trusted LAN is fine.

## Permissions

`PUID` / `PGID` / `UMASK` (defaults `99` / `100` / `002` for Unraid's `nobody:users`) set the owner
and mode of files Optimisarr creates, so replaced media keeps ownership your other apps expect.

## Hardware transcoding

Optimisarr bundles the Intel userspace media stack with `jellyfin-ffmpeg`; Unraid must still expose a
working host kernel driver and render device. The container needs device and group access, but no
additional Intel userspace driver package inside the container.

### Intel QSV / VA-API, or AMD VA-API

Keep the **/dev/dri** device mapping in the template (remove it if the host has no `/dev/dri`). Find
the render group with `stat -c '%g' /dev/dri/renderD128` and, if the app can't open the device, add
`--group-add <that-gid>` under **Extra Parameters**.

### NVIDIA NVENC

Install the **Nvidia-Driver** plugin, then under **Extra Parameters** add `--runtime=nvidia`, and set
these variables (Add another Variable):

- `NVIDIA_VISIBLE_DEVICES=all`
- `NVIDIA_DRIVER_CAPABILITIES=compute,video,utility`

`video` is required — without it NVENC fails with *"Cannot load libnvidia-encode.so.1"* even though
`nvidia-smi` works.

Optimisarr confirms each encoder with a tiny real test encode at startup, so a present-but-broken
driver reads as unavailable rather than failing jobs later.
