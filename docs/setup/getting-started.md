# Getting started

Optimisarr runs as one container. It persists SQLite state in `/config`, reads
libraries in `/data`, writes temporary results to `/work`, and quarantines
originals in `/trash`.

## Deploy

Copy [`compose.example.yml`](../../compose.example.yml), set host paths, then:

```bash
docker compose up -d
curl http://localhost:8787/api/health
```

Keep `/data`, `/work`, and `/trash` on one filesystem when possible so
replacement can use atomic moves. Ensure `PUID` and `PGID` can write all four
mounts.

## First workflow

1. Add a library below `/data` and select its media type and rule profile.
2. Scan it; newly found files are probed in the background.
3. Review the explicit eligibility reason in **Inventory**.
4. Queue a small test set and inspect its verification report.
5. Replace only outputs you have reviewed. Originals remain in **Quarantine**
   until approved or retention purges them.
