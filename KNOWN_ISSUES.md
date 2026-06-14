# Known Issues

Tracked problems that are understood but not yet fully resolved. Each entry notes the impact,
the current safety/mitigation status, and the intended fix. Fixed items are moved to the
CHANGELOG, not kept here.

> Safety note: none of the open issues below can destroy an original file. Originals are only ever
> replaced via the verified replace flow, which quarantines the original first (recoverable by
> rollback), and a failed/again-eligible job never touches the source.

## Open

### 1. GPU transcode not engaging / incomplete multi-vendor hardware support

- **Reported:** On the live `optimisarr:dev` container (WSL2 + RTX 4070), transcodes appear to
  run on CPU rather than the GPU.
- **Confirmed during investigation:**
  - The GPU is genuinely available to the container. `nvidia-smi` works inside the container
    (WSL2 exposes the GPU via `/dev/dxg`, *not* `/dev/nvidia*` or `/dev/dri`, so those missing
    nodes are a red herring), and a direct `hevc_nvenc` test encode ran on the GPU at ~3.9x.
  - The app's own detection (`GET /api/system/hardware`) correctly reports
    `nvidiaRuntimeAvailable: true` with all `h264/hevc/av1_nvenc` encoders `available: true`.
    So **NVENC detection is working** — the not-using-GPU symptom is *not* an NVENC detection
    failure. Root cause for the CPU fallback is still unconfirmed; prime suspects are the
    configured encoder mode (`queue.encoderMode` / per-library setting resolving to CPU) or the
    FFmpeg command builder not applying the selected hardware encoder. **Needs follow-up.**
- **Multi-vendor gap (relevant to "support all GPUs: NVIDIA, Intel, AMD incl. iGPU"):**
  `HardwareCapabilityService` gates Intel QSV and VAAPI (AMD/Intel iGPU) availability solely on
  `Directory.Exists("/dev/dri")` (`HardwareCapabilityParser.HardwarePresent`). On this WSL2 host
  `driDeviceAvailable: false`, so **every QSV and VAAPI encoder is reported unavailable** even
  though the hardware may be usable. The detection model is also NVIDIA-centric: it has no probe
  for AMD (AMF/VAAPI via `/dev/dri/renderD*`) or a real QSV/VAAPI capability check beyond the
  presence of `/dev/dri`. Proper support means (a) detecting render nodes per vendor rather than a
  single directory check, (b) actually probing each hardware encoder (e.g. a tiny `-f null` test
  encode) instead of inferring from device-node presence, and (c) surfacing the chosen encoder in
  job logs so CPU-vs-GPU is visible.
- **Safety:** no impact on the safety model — worst case is slower CPU transcodes; originals are
  never touched except via the verified replace flow.

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
