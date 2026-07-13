# Troubleshooting

## Start with health and logs

```bash
# Liveness: the web process is responding.
curl http://localhost:8787/api/health

# Readiness: SQLite, required writable paths, FFmpeg, and ffprobe are usable.
curl http://localhost:8787/api/ready
docker compose logs --tail=200 optimisarr
```

`/api/ready` returns `503` with a reason when Optimisarr cannot safely start
work. Check the reported path ownership/mount, database, or missing tool before
placing jobs in the queue. Docker's health check uses this readiness endpoint.

Use **Settings → Tools** to verify the required FFmpeg/ffprobe executables, the optional
`libvmaf` measurement capability, and the actual encoder test result. For a failed job,
open Queue details and read the FFmpeg error and verification report before retrying.

Screenshots in this page use fabricated dummy media created for documentation.
No copyrighted material is used.

![Settings Tools tab showing FFmpeg, ffprobe, hardware devices, and encoder availability](../images/optimisarr-settings-tools-dark.png)

## Common causes

| Symptom | Check |
|---|---|
| `/api/ready` returns `503` | Read the JSON reason first. It usually points to an unwritable `/config`, `/work`, or `/trash` mount, a database migration/open failure, or missing FFmpeg/ffprobe. Fix readiness before queueing jobs. |
| Library cannot scan | Container path exists below `/data`; PUID/PGID can read it. |
| Replace fails / "cannot write" | The library folder must be writable by PUID/PGID. Optimisarr checks access when you add or save a library and again during scans; check the reported error and the mount ownership. |
| Replace/approve says dry-run mode is enabled | Dry-run mode is on under **Settings → General → Replacement**. Jobs can still transcode and verify, but originals and quarantined originals are not moved or purged until dry-run is disabled. |
| Jobs do not start | A library's auto-optimise window being closed (its jobs only run in-window), the concurrency limit, activity pause, or free `/work` space. The Queue shows a reason when a backlog is waiting on a window. |
| GPU mode unavailable | Device mapping/NVIDIA toolkit, group permissions, then Tools test encode. |
| Replacement cannot be atomic | Put `/data`, `/work`, and `/trash` on one filesystem or explicitly allow fallback. |
| No rollback available | Original may have been approved or purged by retention; restore from backup. |
| Config import is rejected | The import validates the whole JSON before writing. Check the listed field errors, especially unsupported settings from a newer build, invalid enum names, and auto-enqueue windows that are not `HH:mm`. |
| UI looks stale after updating the image | Refresh the browser tab first; `index.html` is served no-cache, but an already-open SPA can still be running old JavaScript until it reloads. If it persists, confirm the container was recreated and `docker compose logs --tail=200 optimisarr` shows the new startup. |

## Verification failures

Verification failures mean the original is still in place. Read the Queue detail
sheet before retrying:

- **Duration**, **tail integrity**, or **timestamp integrity** failures usually
  indicate a truncated or malformed output.
- **Audio retained**, **subtitle retained**, **A/V sync**, **loudness**, and
  **true peak** failures indicate stream or audio changes outside the configured
  policy.
- **Size reduction** failure means the output was not smaller than the original.
  Either leave the file alone or change the library rules deliberately.
- **VMAF** and **image SSIM/metadata** failures are opt-in quality gates. If
  enabled, missing measurements fail closed.

Use **Retry** only after changing the underlying cause: preset, hardware mode,
source file, mount access, or verification policy. Use **Exclude** for files you
do not want Optimisarr to offer again.
