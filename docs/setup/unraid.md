# Optimisarr on Unraid

Optimisarr runs as a single Docker container. This guide covers installing it on Unraid, the volume
layout that keeps replacements safe, and enabling hardware transcoding.

## Install

Install from [Optimisarr in Community Apps](https://ca.unraid.net/apps/optimisarr-0y5bjeh0aktq6l):

1. Open **Apps**, search for **Optimisarr**, and click **Install**.
2. Review the storage paths, host port and optional hardware device, then click **Apply**.
3. Open the container's **WebUI** to complete first-run setup. Start with a small test library;
   fresh installations use dry-run and do not automatically queue or replace media.

The canonical template is
[`unraid/optimisarr.xml`](../../unraid/optimisarr.xml). For manual installation, download its
[raw XML](https://raw.githubusercontent.com/Jellman86/optimisarr/main/unraid/optimisarr.xml)
to `/boot/config/plugins/dockerMan/templates-user/my-optimisarr.xml` on your Unraid host, then
select it under **Docker → Add Container → Template**. The dropdown selects saved templates;
it is not a field for pasting an arbitrary URL.

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
enabled in **Settings → Files & safety** and use the setup Re-test action to confirm the effective relationship.

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

### NVIDIA NVENC and NVDEC

Install the **Nvidia-Driver** plugin, then under **Extra Parameters** add `--runtime=nvidia`, and set
these variables (Add another Variable):

- `NVIDIA_VISIBLE_DEVICES=all`
- `NVIDIA_DRIVER_CAPABILITIES=compute,video,utility`

`video` is required for hardware encoding and decoding — without it NVENC fails with *"Cannot load
libnvidia-encode.so.1"* even though `nvidia-smi` works, and NVDEC is unavailable.

Optimisarr confirms each encoder with a tiny real test encode at startup, so a present-but-broken
driver reads as unavailable rather than failing jobs later.

## Remote workers

Remote workers are available by default. Fresh installations enable them and require full
sidecar verification; upgrades keep the saved choices, including disabled workers. Pair trusted
Windows, macOS or Linux sidecars from **Settings → Remote workers**. No machine is discovered or
paired automatically. Library placement decides whether a job may run locally or on a worker.

The template's advanced **Remote workers** variable uses the existing
`OPTIMISARR_EXPERIMENTAL_REMOTE_WORKERS` name for compatibility. Set it to `false` to disable the
worker service for the deployment. An unset variable now leaves the service available. You can
also disable new worker activity in **Settings → Files & safety** without deleting pairings.
See [Remote workers and sidecars](remote-workers.md) for placement, strict verification and updates.

## Updates and installation checks

The template uses `:latest`, the released channel. `:dev` is a development build and is not the
same as the latest release. Update from Unraid's Docker/Apps interface after reviewing the
[release notes](https://github.com/Jellman86/optimisarr/releases). Pause new work and let active
jobs finish before stopping/updating: the application can wait for work on graceful shutdown,
but Unraid's configured Docker stop timeout may force termination earlier. Keep `/config` and
the storage-root mapping intact so history and rollback paths survive updates.

After installing or updating, check the following:

- The Docker page reports **healthy**, the **WebUI** opens the mapped host port, and the UI shows
  the expected released version.
- **Settings → System** reports writable config, work and quarantine paths. Setup's Re-test
  confirms the effective filesystem/mount relationship; one mapping does not guarantee that
  underlying Unraid shares, pools or devices share a filesystem.
- **Settings → Tools** proves the intended encoder, full decoding and VMAF capability. On a
  CPU-only host, remove the optional `/dev/dri` entry before applying the template.
- A small test job produces a verification report before you allow replacement. If using a
  sidecar, confirm its attribution and full verification evidence in the job details.

The template and repository profile share the static transparent Precession cube application
icon. The Community Apps listing also links the setup guide, GPL license and fabricated
Dashboard/Queue screenshots. Cached listing text or icons may lag the default-branch template;
existing installations keep their own saved template and settings.
