<p align="center">
  <img src="web/public/favicon-192.png" alt="Optimisarr app icon" width="192">
</p>
<h1 align="center">Optimisarr</h1>
<p align="center"><strong>Safe, verified FFmpeg transcoding for self-hosted media libraries.</strong></p>
<p align="center">
  <a href="#documentation">Docs</a> •
  <a href="#quick-start-docker">Quick Start</a> •
  <a href="#hardware-acceleration-gpu">Hardware Acceleration</a>
</p>

**Optimisarr is a self-hosted, Docker-based FFmpeg media-library optimiser.**
It finds eligible video, audio, and image files, transcodes them to reduce
storage use, verifies the result, and only then replaces the original.

Built for Plex, Jellyfin, Emby, Sonarr, and Radarr users, Optimisarr supports
CPU, NVIDIA NVENC, Intel QSV, and VA-API transcoding. Originals are quarantined
for rollback rather than deleted immediately.

## Why Optimisarr?

- Reduce media-library storage without manually batch-transcoding files.
- Verify output before replacement, with configurable stream-retention,
  duration, and size-reduction checks.
- Run one Docker container on a homelab, Unraid-style server, or other
  self-hosted setup.
- Pause processing while Plex, Jellyfin, or Emby has active streams.

<p align="center">
  <img src="docs/images/optimisarr-queue-dark.png" alt="Optimisarr Queue in dark mode, showing active GPU transcoding" width="100%">
</p>
<p align="center"><sub>All media shown in screenshots is fabricated test data created for documentation; no copyrighted material is used.</sub></p>

## Documentation

Start with the [documentation index](docs/index.md): [getting started](docs/setup/getting-started.md), [user workflow](docs/usage/workflow.md), [configuration](docs/setup/configuration.md), [hardware acceleration](docs/setup/hardware-acceleration.md), [reverse proxy](docs/setup/reverse-proxy.md), [safe replacement](docs/operations/safe-replacement.md), [integrations](docs/integrations/media-servers.md), [troubleshooting](docs/troubleshooting/diagnostics.md), [glossary](docs/glossary.md), and [API reference](docs/api.md).

## Project status

Optimisarr is early-stage software. Use it on a small test set first and keep
backups of media you cannot replace. It is maintained in spare time, so there is
no support SLA or promise of a release schedule.

## What it does

- Multiple **libraries**, each with its own media type (Film/TV/Music/Photo/Other)
  and rule profile, with a folder-picker for paths. Scanning discovers the file
  types that match the library (video, audio, or images).
- Recursive, settling-aware **scanning** that builds a media inventory (idempotent),
  with **automatic background probing** of newly discovered files. Enabled libraries
  are rescanned on a configurable global interval (one hour by default).
- **ffprobe** inspection (codec, resolution, duration, tracks, media kind).
- Optimisation for **video, audio, and still images** (images → WebP), each through
  the same candidate → transcode → verify → quarantine/rollback pipeline.
- FFmpeg/ffprobe **tool detection**, liveness, and readiness endpoints. Docker
  health checks verify database access, required writable paths, and media tools.
- Svelte 5 + Tailwind **sidebar UI** (Dashboard, Libraries, Inventory, Queue,
  Quarantine, Schedule, Settings; Tools live under Settings). Verification reports
  are available from Queue and Quarantine detail sheets.
- Queue resource controls: max concurrent jobs, CPU thread limits, and a free
  work-disk safety pause. The only global scheduling setting is the library scan
  interval; *when* work runs is set per library (see auto-optimise below).
- Per-library **auto-optimise** windows continuously queue newly eligible files;
  opt-in **auto-replace** promotes only fully verified jobs, still quarantining
  the original first so rollback remains available.
- **Optimisation presets** per library (Compatibility H.264 / Balanced HEVC /
  Efficiency AV1 / Remux), plus **Scott's Settings** — HEVC with HDR preserved and
  AAC 96 kbps stereo audio. Optionally **re-encode oversized files already in the
  target codec** (e.g. a huge HEVC remux) above a size you set.
- **Exclude files** so they are never optimised again — manually from a stuck Queue
  job, or **automatically after repeated failures** — managed per library on an
  **Excluded** tab. Durable (keyed by path) and reversible; originals untouched.
- A **Dashboard** leading with a persistent lifetime **space-saved** total (resettable),
  what's in flight, and live CPU/GPU usage while a job encodes.
- **Preview** from Inventory or a library's Candidates tab to try the resolved settings on one
  file before queueing it. Long video previews encode a 60-second sample from the middle of the
  source, verify against a temporary clipped reference from that same window, and label the report
  as segment-only; audio and image previews run in full.
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
- A **"now encoding" hero panel** with a live progress bar, fps/speed/ETA, and a **live CPU/GPU
  usage graph** while a job encodes (sampled with **unprivileged** reads only; no root or extra
  container capabilities). Click any job for a **detail view** showing the resolved encoder, the
  exact **FFmpeg command**, the verification report, and inline actions (retry, exclude, replace).
  The sidebar's Queue item throbs a **GPU chip** when work is hardware-accelerated or a **snail**
  when it's on the CPU, with a running-job count.
- Optional **service-activity pauses** (Plex/Jellyfin/Emby), **dry-run mode**,
  configurable replacement/quarantine policy with a retention window, and **library
  integrations** (Plex/Jellyfin/Emby re-scan, Sonarr/Radarr import-aware exclusions,
  notifications, config-and-secrets backup/import).

Still planned (see the [roadmap](docs/roadmap.md)): real-hardware validation for
AMD VA-API. Intel QSV has been tested on real hardware for both encoding and
decoding.

## Before you start

- Docker Engine with the Compose plugin.
- Read and write access to the media folders you mount into the container.
- `/data`, `/work`, and `/trash` on the same filesystem if you want atomic
  replacement moves.
- A backup of media that matters to you. Quarantine and rollback are useful,
  but they are not a backup strategy.

## Quick start (Docker)

The image is published to GHCR on every push to `dev`:

```bash
mkdir -p ./optimisarr-config /path/to/work /path/to/trash
sudo chown -R 1000:1000 ./optimisarr-config /path/to/work /path/to/trash
```

```bash
docker run -d --name optimisarr \
  -p 8787:8787 \
  -e PUID=1000 -e PGID=1000 -e TZ=Europe/London \
  -e OPTIMISARR_ADMIN_TOKEN='change-this-long-random-token' \
  -v ./optimisarr-config:/config \
  -v /path/to/media:/data \
  -v /path/to/work:/work \
  -v /path/to/trash:/trash \
  ghcr.io/jellman86/optimisarr:dev
```

Wait for readiness, then open the UI:

```bash
curl http://localhost:8787/api/ready
```

Open `http://localhost:8787`, enable **Dry-run mode** in **Settings → General → Replacement**,
add a library on the **Libraries** page, then scan and queue a small test set.
Compose examples are available for every supported runtime:

- [CPU only](compose.cpu.example.yml)
- [NVIDIA NVENC](compose.nvidia.example.yml)
- [Intel QSV](compose.intel-qsv.example.yml)
- [Intel or AMD VA-API](compose.vaapi.example.yml)
- [combined reference file](compose.example.yml)

Keep `/data`, `/work`, and `/trash` on the **same filesystem** so the
replacement pipeline can use atomic moves. Do not publish `8787` directly to the
internet; use an authenticated reverse proxy for remote access. Setting
`OPTIMISARR_ADMIN_TOKEN` adds a built-in bearer-token backstop for the UI and API,
but a reverse proxy remains the recommended public-access boundary.

## Hardware acceleration (GPU)

Transcoding runs through a bundled **jellyfin-ffmpeg**, which ships NVENC plus the Intel
iHD driver and oneVPL runtime — so NVIDIA, Intel (incl. iGPUs like the N100), and AMD GPUs
work without installing host driver packages. The encoder is picked by the global **encoder
mode** (Settings → Auto by default); the **Tools** page shows what each GPU actually supports
(availability is confirmed by a real test encode), and each Queue job shows whether it ran on
the **GPU** or **CPU**. Perceptual quality measurement uses a separate, pinned static FFmpeg
with `libvmaf`; the Tools page reports that optional capability independently.
VMAF verification is enabled by default for video re-encodes and skipped for remuxes;
it can be disabled under **Settings → Verification gates** when throughput is preferred.

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

Use the matching Compose example: [NVIDIA NVENC](compose.nvidia.example.yml),
[Intel QSV](compose.intel-qsv.example.yml), or
[Intel/AMD VA-API](compose.vaapi.example.yml). A
[CPU-only file](compose.cpu.example.yml) is also provided.

## Development

Standards and commands live in [`CLAUDE.md`](CLAUDE.md). In short:

```bash
dotnet build Optimisarr.slnx      # backend
dotnet test  Optimisarr.slnx      # tests
cd web && npm run check           # frontend type/lint check
cd web && npm run dev             # frontend dev server (proxies /api to :8787)
```

## License

Optimisarr is licensed under the [GNU General Public License v3.0](LICENSE). The
published Docker image also bundles GPL-licensed FFmpeg distributions
([jellyfin-ffmpeg](https://github.com/jellyfin/jellyfin-ffmpeg) and
[static-ffmpeg](https://github.com/wader/static-ffmpeg)), which remain under their own licenses.

## Project references

- [Changelog](CHANGELOG.md)
- [Product and architecture](docs/product-and-architecture.md)
- [Roadmap](docs/roadmap.md)
- [Engineering standards](CLAUDE.md)
- [Security policy](SECURITY.md)
- [Support](SUPPORT.md)
- [Contributing](CONTRIBUTING.md)
- [Code of Conduct](CODE_OF_CONDUCT.md)
