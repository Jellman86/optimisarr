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
| Encoder mode | Auto, CPU, NVIDIA NVENC, Intel QSV, or VA-API. |

Per-library auto-enqueue fills the queue on schedule but still obeys global
window, concurrency, and disk-safety controls. Quarantine retention is not a
backup policy; retain independent backups of irreplaceable media and `/config`.
