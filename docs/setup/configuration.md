# Configuration and scheduling

Settings are stored in `/config/optimisarr.db`; idempotent EF Core migrations
run at startup.

Each library has its own root, media type, rule profile, and optional overrides.
The Inventory explains why every file is eligible or skipped.

| Control | Behaviour |
|---|---|
| Processing window | Starts new work only inside the window; running work continues. |
| Concurrent jobs | Bounds parallel encodes. |
| CPU threads | Limits FFmpeg CPU usage where applicable. |
| Work-disk threshold | Prevents new starts when `/work` is too full. |
| Library scan interval | Rescans every enabled library at the configured interval (one hour by default). Scanning runs once at startup and is independent of auto-optimise. |
| Encoder mode | Auto, CPU, NVIDIA NVENC, Intel QSV, or VA-API. |

## Per-library automation

**Auto-optimise** continuously evaluates and queues eligible files while its
per-library local-time window is open. It does not scan or start work itself:
the independent scan/probe workers discover files, and Queue dispatch still
obeys the global processing window, concurrency, activity-pause, and disk-safety
controls. A start time equal to the end time means the auto-optimise window is
open all day.

**Auto-replace** is disabled by default. When enabled for a library, a job that
passes every verification gate is replaced automatically. The original is still
quarantined first and remains rollback-able through **Quarantine**. Enable it
only after validating a small manual batch for that library.

Quarantine retention is not a backup policy; retain independent backups of
irreplaceable media and `/config`.
