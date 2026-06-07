# optimisarr

Safe media library optimiser with GPU transcoding, scheduling, and verified
replacement. Optimisarr never deletes or replaces an original until a converted
file has passed explicit verification gates.

## Status

Early development. What works today:

- Multiple **libraries**, each with its own media type (Film/TV/Music/Other) and
  rule profile, with a folder-picker for paths.
- Recursive, settling-aware **scanning** that builds a media inventory (idempotent).
- **ffprobe** inspection of individual files (codec, resolution, duration, tracks).
- FFmpeg/ffprobe **tool detection** and a health endpoint.
- Svelte 5 + Tailwind **sidebar UI** (Dashboard, Libraries, Inventory, Tools).

Not built yet (see the [roadmap](docs/roadmap.md)): candidate rules, the
transcode queue and worker, verification, safe replacement/rollback, scheduling,
and GPU encoder selection.

## Quick start (Docker)

The image is published to GHCR on every push to `dev`:

```bash
docker run -d --name optimisarr \
  -p 8787:8787 \
  -e PUID=1000 -e PGID=1000 -e TZ=Europe/London \
  -v /path/to/config:/config \
  -v /path/to/media:/data \
  -v /path/to/work:/work \
  -v /path/to/trash:/trash \
  ghcr.io/jellman86/optimisarr:dev
```

Then open `http://localhost:8787`, add a library on the **Libraries** page, and
scan it. See [`compose.example.yml`](compose.example.yml) for a Compose setup
(including the optional `/dev/dri` GPU mapping).

Keep `/data`, `/work`, and `/trash` on the **same filesystem** so the future
replacement pipeline can use atomic moves.

## Development

Standards and commands live in [`CLAUDE.md`](CLAUDE.md). In short:

```bash
dotnet build Optimisarr.slnx      # backend
dotnet test  Optimisarr.slnx      # tests
cd web && npm run check           # frontend type/lint check
cd web && npm run dev             # frontend dev server (proxies /api to :8787)
```

## Planning

- [Changelog](CHANGELOG.md)
- [Product and architecture](docs/product-and-architecture.md)
- [Roadmap](docs/roadmap.md)
- [Engineering standards](CLAUDE.md)
