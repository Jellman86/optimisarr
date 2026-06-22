<p align="center">
  <img src="web/public/favicon-192.png" alt="Optimisarr app icon" width="96">
</p>
<h1 align="center">Optimisarr</h1>
<p align="center"><strong>Safe, verified optimisation for self-hosted media libraries.</strong></p>
<p align="center">
  <a href="#documentation">Docs</a> •
  <a href="#quick-start-docker">Quick Start</a> •
  <a href="#hardware-acceleration-gpu">Hardware Acceleration</a>
</p>

Safe media library optimiser with GPU transcoding, scheduling, and verified
replacement. Optimisarr never deletes or replaces an original until a converted
file has passed explicit verification gates.

## Documentation

Start with the [documentation index](docs/index.md): [getting started](docs/setup/getting-started.md), [configuration](docs/setup/configuration.md), [hardware acceleration](docs/setup/hardware-acceleration.md), [safe replacement](docs/operations/safe-replacement.md), [integrations](docs/integrations/media-servers.md), and [troubleshooting](docs/troubleshooting/diagnostics.md).

## Current capabilities

Early development. What works today:

- Multiple **libraries**, each with its own media type (Film/TV/Music/Photo/Other)
  and rule profile, with a folder-picker for paths. Scanning discovers the file
  types that match the library (video, audio, or images).
- Recursive, settling-aware **scanning** that builds a media inventory (idempotent),
  with **automatic background probing** of newly discovered files. Enabled libraries
  are rescanned on a configurable global interval (one hour by default).
- **ffprobe** inspection (codec, resolution, duration, tracks, media kind).
- Optimisation for **video, audio, and still images** (images → WebP), each through
  the same candidate → transcode → verify → quarantine/rollback pipeline.
- FFmpeg/ffprobe **tool detection** and a health endpoint.
- Svelte 5 + Tailwind **sidebar UI** (Dashboard, Libraries, Inventory, Queue,
  Verification, Quarantine, Schedule, Settings; Tools live under Settings).
- Queue resource controls: max concurrent jobs, CPU thread limits, processing
  windows, and free work-disk safety pause.
- Per-library **auto-optimise** windows continuously queue newly eligible files;
  opt-in **auto-replace** promotes only fully verified jobs, still quarantining
  the original first so rollback remains available.
- Hardware capability detection for FFmpeg accelerators, CPU encoders, NVIDIA
  NVENC, Intel QSV, VAAPI, NVIDIA runtime, and `/dev/dri` mapping.
- Global encoder mode selection for Auto, CPU, NVIDIA NVENC, Intel QSV, and VAAPI.
- Configurable verification gates for duration tolerance, audio/subtitle
  retention, and required size reduction.

- **Hardware transcoding** through NVIDIA NVENC, Intel QSV, and Intel/AMD VA-API, with
  per-encoder availability **confirmed by a real test encode** (not just inferred), and the
  encoder used shown per job (GPU/CPU) on the Queue.
- **GPU hardware decoding** (QSV/VA-API) of the source as well as the encode, on by default,
  with automatic CPU-decode fallback for sources the GPU can't decode — so a large 4K encode no
  longer burns a CPU core just on software decode. Skipped for HDR→SDR tonemap jobs (the tonemap
  runs in software).
- A **Queue detail view**: click any job for a live progress bar, fps/speed/ETA, the resolved
  encoder, the verification report, and inline actions — plus a **live CPU/GPU usage graph** while
  it encodes (sampled with **unprivileged** reads only; no root or extra container capabilities).
  The sidebar's Queue item throbs a **GPU chip** when work is hardware-accelerated or a **snail**
  when it's on the CPU, with a running-job count.
- Optional **service-activity pauses** (Plex/Jellyfin/Emby), configurable
  replacement/quarantine policy with a retention window, and **library integrations**
  (Plex/Jellyfin/Emby re-scan, Sonarr/Radarr import-aware exclusions, notifications,
  secret-free config import/export).

Not built yet (see the [roadmap](docs/roadmap.md)): release hardening (dry-run, config
backup). **Intel QSV is now validated on real hardware** (hardware encode *and* decode);
**AMD VA-API** validation is still pending a real AMD GPU.

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

Keep `/data`, `/work`, and `/trash` on the **same filesystem** so the
replacement pipeline can use atomic moves.

## Hardware acceleration (GPU)

Transcoding runs through a bundled **jellyfin-ffmpeg**, which ships NVENC plus the Intel
iHD driver and oneVPL runtime — so NVIDIA, Intel (incl. iGPUs like the N100), and AMD GPUs
work without installing host driver packages. The encoder is picked by the global **encoder
mode** (Settings → Auto by default); the **Tools** page shows what each GPU actually supports
(availability is confirmed by a real test encode), and each Queue job shows whether it ran on
the **GPU** or **CPU**.

When a hardware encoder is in use the source is **hardware-decoded** on the GPU too
(Settings → *Hardware decoding*, on by default), so frames never round-trip through system
memory. If the GPU can't decode a particular source, the job automatically retries with software
decode rather than failing. The Queue detail view shows a live CPU/GPU usage graph while a job
runs — GPU stats are read **without any elevated privileges** (per-process DRM fdinfo for
Intel/AMD, `nvidia-smi` for NVIDIA), so **no extra container capability or compose change is
needed**; hosts where no unprivileged source applies simply show "GPU stats unavailable".

- **NVIDIA (NVENC):** install the [NVIDIA Container Toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/install-guide.html)
  on the host and run with `--gpus all`. You **must** also set
  `NVIDIA_DRIVER_CAPABILITIES=compute,video,utility` — without the `video` capability the NVENC
  library isn't injected and encoding fails with `Cannot load libnvidia-encode.so.1` even though
  `nvidia-smi` works.
- **Intel (QSV / VA-API) and AMD (VA-API):** map the render node and add the container to the
  host's `render` group:

  ```bash
  docker run -d --name optimisarr \
    --device /dev/dri:/dev/dri \
    --group-add "$(getent group render | cut -d: -f3)" \
    ... ghcr.io/jellman86/optimisarr:dev
  ```

See [`compose.example.yml`](compose.example.yml) for the equivalent Compose blocks.

## Development

Standards and commands live in [`CLAUDE.md`](CLAUDE.md). In short:

```bash
dotnet build Optimisarr.slnx      # backend
dotnet test  Optimisarr.slnx      # tests
cd web && npm run check           # frontend type/lint check
cd web && npm run dev             # frontend dev server (proxies /api to :8787)
```

## Project references

- [Changelog](CHANGELOG.md)
- [Product and architecture](docs/product-and-architecture.md)
- [Roadmap](docs/roadmap.md)
- [Engineering standards](CLAUDE.md)
- [Documentation generation prompt](docs/DOCUMENTATION_PROMPT.md)
