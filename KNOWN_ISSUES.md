# Known Issues

Tracked problems that are understood but not yet fully resolved. Each entry notes the impact,
the current safety/mitigation status, and the intended fix. Fixed items are moved to the
CHANGELOG, not kept here.

> Safety note: none of the open issues below can destroy an original file. Originals are only ever
> replaced via the verified replace flow, which quarantines the original first (recoverable by
> rollback), and a failed/again-eligible job never touches the source.

## Open

### 1. Hardware encoding: NVENC fixed; QSV/VAAPI pending on-hardware validation

- **Root cause found and fixed (NVENC).** Transcodes ran on CPU because the worker resolved a
  hardware encoder only when a file's `MediaKind` was exactly `Video`, while the command builder
  treats any non-audio/image spec as a video re-encode. A video classified `Unknown` (a row probed
  before media-kind detection existed) skipped encoder selection and silently used the CPU library
  encoder. Encoder resolution is now gated on the spec actually re-encoding video, the chosen
  encoder is logged per job, and the command builder emits per-encoder rate control (NVENC `-cq`,
  QSV `-global_quality`, VAAPI `-rc_mode CQP -qp`) instead of `-crf` for everything. Verified live
  on WSL2 + RTX 4070: a Conservative-HEVC job now runs `hevc_nvenc` on the GPU. A one-time backfill
  re-probes legacy `Unknown` files so they classify correctly.
- **Intel (N100) / AMD — implemented, not yet validated on hardware.** Transcoding and detection now
  run through jellyfin-ffmpeg, which bundles the Intel iHD driver + oneVPL (libvpl) and NVENC, so no
  host driver packages are needed; the compose example documents `/dev/dri` + the `render` group.
  The QSV/VAAPI command shape (`-init_hw_device qsv=hw` / `-vaapi_device` + `format=nv12,hwupload`)
  is unit-tested but has **not been run on a real Intel iGPU or AMD GPU yet** — validate on the N100
  when available, in particular the QSV device-init/upload filter and the VAAPI render-node path.
- **Detection caveat (unchanged):** QSV/VAAPI availability is still gated on `Directory.Exists("/dev/dri")`.
  That is correct on a real N100/AMD host that maps the render node, but means QSV/VAAPI always read
  unavailable on this WSL2 dev host (the GPU is exposed via `/dev/dxg`, not `/dev/dri`). A true
  per-encoder probe (a tiny `-f null` test encode) instead of inferring from device-node presence is
  future work.
- **Safety:** no impact on the safety model — worst case is a hardware job that fails fast or falls
  back to CPU; originals are never touched except via the verified replace flow.

### 2. Animated images — partially addressed

- **Status:** Animated GIF/WebP files are now **skipped as candidates** (detected via the probed
  frame count), so they are no longer flattened into a broken single-frame output. Previously such
  a job produced a malformed, larger output that verification correctly failed (original
  untouched).
- **Remaining:** animated images are simply left alone. Proper animated-to-modern-format
  conversion (e.g. animated WebP/AV1) is future work; for now they are out of scope for the still
  pipeline.

## Resolved recently

These were found during live testing and fixed (see CHANGELOG for details):

- Output filename collision when two sources shared a stem (e.g. `photo.bmp` and `photo.tif`, both
  → `photo.webp`). The work output is now namespaced per media file (`/work/<id>/…`) so jobs never
  clobber each other's output, and a replacement whose destination is already occupied by a
  different file now fails safely with a clear reason, leaving the original untouched.
- The image optimisation marker did not round-trip (ffmpeg's still encoders drop `-metadata`). It is
  now written and read with exiftool in the EXIF/XMP `Software` field, so the marker is portable for
  JPEG/WebP/AVIF — surviving a database wipe or a move to another machine.
- Non-video libraries discovered no files (scanner was video-only).
- Discovered files were never probed automatically, so a scanned library produced no candidates
  and could not be enqueued.
- The video rule profile (e.g. `ConservativeHevc`) was shown for Music/Photo libraries on the
  Libraries page and as the "Profile" column for audio/image rows on the Candidates page, where it
  is meaningless.
