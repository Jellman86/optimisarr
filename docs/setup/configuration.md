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

Quarantine retention is not a backup policy; retain independent backups of
irreplaceable media and `/config`.

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
