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

Use **Settings → Tools** to verify the FFmpeg/ffprobe executable and the actual
encoder test result. For a failed job, open Queue details and read the FFmpeg
error and verification report before retrying.

## Common causes

| Symptom | Check |
|---|---|
| Library cannot scan | Container path exists below `/data`; PUID/PGID can read it. |
| Replace fails / "cannot write" | The library folder must be writable by PUID/PGID. Optimisarr checks access when you add or save a library and again during scans; check the reported error and the mount ownership. |
| Jobs do not start | A library's auto-optimise window being closed (its jobs only run in-window), the concurrency limit, activity pause, or free `/work` space. The Queue shows a reason when a backlog is waiting on a window. |
| GPU mode unavailable | Device mapping/NVIDIA toolkit, group permissions, then Tools test encode. |
| Replacement cannot be atomic | Put `/data`, `/work`, and `/trash` on one filesystem or explicitly allow fallback. |
| No rollback available | Original may have been approved or purged by retention; restore from backup. |
