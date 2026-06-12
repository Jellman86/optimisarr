# optimisarr

Safe media library optimiser with GPU transcoding, scheduling, and verified
replacement. Optimisarr never deletes or replaces an original until a converted
file has passed explicit verification gates.

## Status

Early development. What works today:

- Multiple **libraries**, each with its own media type (Film/TV/Music/Photo/Other)
  and rule profile, with a folder-picker for paths. Scanning discovers the file
  types that match the library (video, audio, or images).
- Recursive, settling-aware **scanning** that builds a media inventory (idempotent),
  with **automatic background probing** of newly discovered files.
- **ffprobe** inspection (codec, resolution, duration, tracks, media kind).
- Optimisation for **video, audio, and still images** (images → WebP), each through
  the same candidate → transcode → verify → quarantine/rollback pipeline.
- FFmpeg/ffprobe **tool detection** and a health endpoint.
- Svelte 5 + Tailwind **sidebar UI** (Dashboard, Libraries, Inventory, Tools,
  Queue, Quarantine, Settings).
- Queue resource controls: max concurrent jobs, CPU thread limits, processing
  windows, and free work-disk safety pause.
- Hardware capability detection for FFmpeg accelerators, CPU encoders, NVIDIA
  NVENC, Intel QSV, VAAPI, NVIDIA runtime, and `/dev/dri` mapping.
- Global encoder mode selection for Auto, CPU, NVIDIA NVENC, Intel QSV, and VAAPI.
- Configurable verification gates for duration tolerance, audio/subtitle
  retention, and required size reduction.

Not built yet (see the [roadmap](docs/roadmap.md)): optional service-activity
pauses, configurable replacement/quarantine policy, hardware-specific encoder
quality notes, and library integrations.

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
