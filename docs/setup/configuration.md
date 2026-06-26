# Configuration and scheduling

Settings are stored in `/config/optimisarr.db`; idempotent EF Core migrations
run at startup.

Each library has its own root, media type, rule profile, and optional overrides.
The Inventory explains why every file is eligible or skipped.

| Control | Behaviour |
|---|---|
| Library scan interval | Rescans every enabled library at the configured interval (one hour by default), the only scheduling control in global settings. Scanning also runs once at startup. |
| Concurrent jobs | Bounds parallel encodes. |
| CPU threads | Limits FFmpeg CPU usage where applicable. |
| Work-disk threshold | Prevents new starts when `/work` is too full. |
| Encoder mode | Auto, CPU, NVIDIA NVENC, Intel QSV, or VA-API. |

There is no global processing window: *when* work runs is set per library (see
below). Jobs you queue manually run whenever the queue can start one.

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

Import validates the complete file before writing, then merges configuration
without deleting existing entries. It intentionally does not include media,
queued jobs, replacements, quarantined originals, or rollback history. Keep a
separate backup of `/config/optimisarr.db` and `/trash` when that operational
state must be recoverable.
