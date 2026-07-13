# Configuration and scheduling

Settings are stored in `/config/optimisarr.db`; idempotent EF Core migrations
run at startup.

Screenshots in this page use fabricated dummy media created for documentation.
No copyrighted material is used.

![Settings General tab showing queue, encoder, scan interval, disk threshold, and hardware decode controls](../images/optimisarr-settings-general-dark.png)

## Admin token

Optimisarr is intended to run on a trusted network or behind an authenticated
reverse proxy. For a built-in backstop, set `OPTIMISARR_ADMIN_TOKEN` to a long
random value before starting the container:

```yaml
environment:
  OPTIMISARR_ADMIN_TOKEN: "change-this-long-random-token"
```

When the token is set, the web UI asks for it before loading operational data.
API clients must send it as a bearer token:

```bash
curl -H "Authorization: Bearer change-this-long-random-token" \
  http://localhost:8787/api/settings
```

`/api/health`, `/api/ready`, and `/api/auth/status` remain open for health
checks and startup detection. If the token is not set, Optimisarr behaves as it
did before and logs a warning at startup.

Each library has its own root, media type, rule profile, and optional overrides.
The Inventory explains why every file is eligible or skipped.

| Control | Behaviour |
|---|---|
| Library scan interval | Rescans every enabled library at the configured interval (one hour by default), the only scheduling control in global settings. Scanning also runs once at startup. |
| Concurrent jobs | Bounds parallel encodes. |
| CPU threads | Limits FFmpeg CPU usage where applicable. |
| Work-disk threshold | Prevents new starts when `/work` is too full. |
| Encoder mode | Auto, CPU, NVIDIA NVENC, Intel QSV, or VA-API. |
| Hardware decoding | Uses GPU decode with hardware encoders when possible, then falls back to CPU decode for sources the GPU cannot decode. |

There is no global processing window: *when* work runs is set per library (see
below). Jobs you queue manually run whenever the queue can start one.

## Verification gates

Every job must pass decode health, output readability, and the media-kind checks
that apply to it. The configurable gates make replacement stricter:

![Verification gates panel showing always-on checks, VMAF, loudness, true peak, image SSIM, and metadata controls](../images/optimisarr-settings-verification-dark.png)

| Gate | Applies to | Default |
|---|---|---|
| Duration tolerance | Video and audio | On, 1% |
| Require audio tracks retained | Video and audio | On |
| Require subtitle tracks retained | Video | Off |
| Require output smaller than original | Video, audio, image | On |
| Perceptual quality (VMAF) | Video re-encodes | On, harmonic mean 93 / worst frame 80 |
| Audio loudness drift (EBU R128) | Video and audio | Off |
| Audio clipping (true peak) | Video and audio | Off |
| Image SSIM | Images | Off |
| Image metadata | Images | Off |

Enabled measurement gates fail closed. If Optimisarr cannot measure an enabled
VMAF, loudness, true-peak, SSIM, or metadata gate, the job fails instead of
becoming replaceable. VMAF is skipped for remux-only work because those jobs copy
the encoded video frames unchanged. Existing installations retain their saved VMAF
choice; a new installation starts with the safer gate enabled.

No libvmaf model or filter configuration is required in the UI. Optimisarr prepares
both streams at the original's resolution with bicubic scaling, aligns their
timebases and starting timestamps, normalises colour range and pixel format, and
uses bounded automatic threading. It selects Netflix's `vmaf_v0.6.1` HDTV model
for HD material and `vmaf_4k_v0.6.1` when either source axis reaches UHD. If a job
intentionally converts HDR to SDR, the reference receives the same production
tone-map before comparison; HDR-preserving jobs keep both streams in the matching
HDR transfer domain. The model and preparation used are recorded in the result.

The 93 harmonic-mean and 80 worst-frame floors are Optimisarr's conservative
replacement guardrails, not universal scores promised by Netflix. VMAF is most
useful for compression and scaling damage; the independent decode, duration,
stream, HDR-signal, colour, timestamp, and A/V-sync checks remain equally important.
Netflix does not publish a general HDR VMAF model: for HDR-preserving work Optimisarr
compares both streams in the same HDR transfer domain, which remains a useful
full-reference compression check, but its absolute threshold is less formally
calibrated than the SDR viewing models. The default general-purpose profiles exclude
HDR; preserving or tone-mapping it is an explicit library-profile choice.

## Rule profiles (presets)

Each library picks an **optimisation preset** that sets its codec, container, and a
researched quality target; anything can be fine-tuned under **Advanced options**.

| Preset | Targets |
|---|---|
| Compatibility (H.264) | H.264 / MP4 — plays everywhere, larger files. |
| Balanced (HEVC) | HEVC (H.265) / MP4 at CRF 24 — a good default. |
| Efficiency (AV1) | AV1 / MKV — smallest files, slower to encode. |
| **Scott's Settings** | HEVC / MP4 at CRF 24, **HDR preserved**, audio re-encoded to **AAC 96 kbps downmixed to stereo**. A compatibility-first, space-saving bundle; the same AAC 96 kbps stereo target applies to a music library. |
| Remux / cleanup | No re-encode — repackage into a clean container only. |

A file already in the target codec is normally skipped. Enable **"Re-encode large
files already in the target codec"** (Advanced options) to also re-encode oversized
same-codec files above a size you set (default 20 GB) — useful for shrinking a huge
HEVC remux under an HEVC preset. The size-saving verification gate still rejects an
output that does not get smaller, so the original is never lost.

### Audio channel and bitrate policy

For music and any opted-in video-audio re-encode, the configured bitrate is the budget for a
mono/stereo programme. When Optimisarr retains surround audio it applies that budget per channel
pair: for example, a 128 kbps baseline becomes 384 kbps for 5.1 and 512 kbps for 7.1. Enabling the
explicit stereo downmix keeps the configured value. This conservative scaling prevents a setting
chosen for stereo from starving retained surround channels, and the candidate saving calculation
uses the same effective value. MP3 requires stereo downmix for sources above two channels; AAC and
Opus accept up to eight retained channels. Post-encode verification independently rejects any
unrequested channel loss.

## Per-library automation

**Auto-optimise** uses a per-library local-time window. Inside that window the
library's eligible files are continuously queued **and** dispatched; outside it,
that library's jobs do not start (a running job is never interrupted). Libraries
without auto-optimise have no window, so their manually queued jobs run at any
time. Scanning/probing is independent and global (see the scan interval above),
and Queue dispatch still obeys concurrency, activity-pause, and disk-safety
controls. A start time equal to the end time means the window is open all day.

**Auto-replace** is disabled by default. When enabled for a library, a job that
passes every verification gate is replaced automatically. The original is still
quarantined first and remains rollback-able through **Quarantine**. Enable it
only after validating a small manual batch for that library.

**Dry-run mode** is a global replacement safety switch. It leaves scanning,
queueing, transcoding, verification, previews, and rollback available, but blocks
manual replacement, auto-replace, and quarantine purge. Use it for first passes
over a real library when you want evidence without any original-file changes.

Quarantine retention is not a backup policy; retain independent backups of
irreplaceable media and `/config`.

## Excluded files

You can exclude individual files so they are never optimised. From a failed or
stuck job on the **Queue** page, choose **Exclude**; the file is added to a durable
exclusion list and its failed attempt is cleared. A file that fails three times is
**excluded automatically**. Excluded files are skipped by scans, the candidate
list, and auto-optimise.

Each library has an **Excluded** tab listing its exclusions — automatic ones (from
repeated failures) and manual ones are shown distinctly. Remove an exclusion there
to make the file eligible again (which also resets its failure count). Exclusions
are keyed by file path, so they survive clearing the queue, re-scanning, and
re-adding the library. Originals are never touched either way.

## Configuration backup and import

The **Settings** page can export and import a JSON configuration snapshot. It
includes libraries, activity watchers, notification targets, Arr connections,
and provider credentials in plain text. Store it as sensitive material: do not
commit, share, or leave it in an unprotected download directory.

![Backup tab showing export and import controls plus the sensitive-data warning](../images/optimisarr-settings-backup-dark.png)

Import validates the complete file before writing, then merges configuration
without deleting existing entries. It intentionally does not include media,
queued jobs, replacements, quarantined originals, or rollback history. Keep a
separate backup of `/config/optimisarr.db` and `/trash` when that operational
state must be recoverable.
