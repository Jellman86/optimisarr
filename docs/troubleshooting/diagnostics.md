# Troubleshooting

## Start with health and logs

```bash
curl http://localhost:8787/api/health
docker compose logs --tail=200 optimisarr
```

Use **Settings → Tools** to verify the FFmpeg/ffprobe executable and the actual
encoder test result. For a failed job, open Queue details and read the FFmpeg
error and verification report before retrying.

## Common causes

| Symptom | Check |
|---|---|
| Library cannot scan | Container path exists below `/data`; PUID/PGID can read it. |
| Replace fails / "cannot write" | The library folder must be writable by PUID/PGID. Use **Test access** on the Libraries page to confirm read + write before queueing. |
| Jobs do not start | Processing window, queue limit, activity pause, and free `/work` space. |
| GPU mode unavailable | Device mapping/NVIDIA toolkit, group permissions, then Tools test encode. |
| Replacement cannot be atomic | Put `/data`, `/work`, and `/trash` on one filesystem or explicitly allow fallback. |
| No rollback available | Original may have been approved or purged by retention; restore from backup. |
