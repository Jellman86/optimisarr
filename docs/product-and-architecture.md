# Optimisarr Product and Architecture

## Product intent

Optimisarr is a safe media library optimiser for self-hosted users who want the
space savings of automated transcoding without trusting a black-box tool to
rewrite their library.

The core promise is:

> Optimisarr never deletes or replaces an original until the converted file has
> passed explicit verification gates.

## Target user

- Runs Plex, Jellyfin, Emby, Sonarr, Radarr, or a similar media stack.
- Uses Docker Compose or Unraid-style Docker templates.
- Has media bind-mounted into containers.
- Wants scheduled background conversion using CPU or GPU.
- Cares about preserving subtitles, audio tracks, HDR metadata, and playback
  compatibility.

## MVP scope

### In scope

- Single Docker container.
- Svelte 5 web interface.
- ASP.NET Core minimal-API backend.
- SQLite state stored under `/config`.
- FFmpeg and ffprobe based scanning, probing, transcoding, and verification.
- Configurable library roots mounted under `/data`.
- Work directory for temporary outputs mounted under `/work`.
- Quarantine/trash directory for originals mounted under `/trash`.
- Scheduler windows, pause/resume, and queue priorities.
- GPU capability detection and encoder selection.
- Safe replacement pipeline:
  1. Probe original.
  2. Decide whether the file is eligible.
  3. Transcode to `/work`.
  4. Probe output.
  5. Run health verification.
  6. Compare stream policy.
  7. Replace original atomically when possible.
  8. Move original to quarantine.
  9. Record rollback metadata.

### Out of scope for MVP

- Distributed workers.
- Cloud storage.
- Full plugin marketplace.
- Automatic download client integration.
- Multi-user RBAC.
- Commercial post-production workflows.

## Differentiation

Existing tools are powerful but either broad, complex, or insufficiently
focused on safe replacement.

- Unmanic is open source and plugin-driven, but its strength is broad library
  processing rather than a safety-first replacement workflow.
- Tdarr has mature health checks and worker orchestration, but the server/node
  model and plugin stacks are heavy for a single-host home-lab deployment.
- FileFlows is a powerful general file-processing platform, but its flow-builder
  model is broader than the focused media-library optimisation problem.
- HandBrake Web and docker-handbrake are strong batch/manual transcoders, but
  they are not primarily library-replacement tools.

Optimisarr should win by being boring, predictable, and explicit about what it
will and will not modify.

## Recommended tech stack

### Runtime architecture

Use one container with one ASP.NET Core host:

- ASP.NET Core serves the JSON API, SignalR progress updates, health endpoints,
  and built Svelte assets.
- Background services run scanner, scheduler, and worker loops in the same host.
- SQLite-backed job leases prevent duplicate work and make interrupted jobs
  recoverable after restarts.
- FFmpeg and ffprobe run as child processes with explicit argument arrays,
  cancellation tokens, and captured stdout/stderr.

This avoids multi-container complexity while using the .NET host model for the
parts this app depends on most: long-running background tasks, graceful shutdown,
typed configuration, structured logging, and real-time UI updates.

### Backend

- C# on current LTS .NET.
- ASP.NET Core minimal APIs.
- SignalR for live job progress and queue updates.
- `BackgroundService`/`IHostedService` for scanner, scheduler, and worker loops.
- EF Core with SQLite in `/config/optimisarr.db`.
- Fluent validation or built-in options validation for settings.
- Serilog or Microsoft.Extensions.Logging JSON console logs.
- FFmpeg/ffprobe invoked through `System.Diagnostics.Process`, never through
  shell interpolation.

Why C#/.NET:

- The application is closer to a daemon than a simple request/response API.
- ASP.NET Core has first-class hosted background services.
- SignalR gives a clean real-time channel for progress, logs, and queue changes.
- EF Core plus SQLite gives typed persistence and migrations without an external
  database.
- Strong typing helps keep media stream policies, replacement decisions, and
  verification results explicit and testable.
- Official .NET container images are well supported and multi-arch friendly.

Tradeoffs:

- Python has faster prototyping and broad media scripting examples, but the app
  will quickly need robust job orchestration more than ad-hoc scripting.
- Go would produce a smaller binary and is excellent for filesystem/process work,
  but ASP.NET Core gives a richer web/backend framework, SignalR, and EF Core
  without extra assembly.
- Node would pair naturally with Svelte, but it is a weaker fit for a
  safety-critical media daemon with heavy subprocess orchestration.

### Frontend

- Svelte 5 with Vite.
- TypeScript.
- SPA build emitted as static assets.
- API client generated or typed from shared OpenAPI types later.
- Tailwind CSS is acceptable, but keep the UI dense and operational rather than
  marketing-style.

Why not SvelteKit as the primary server:

- SvelteKit with adapter-node is excellent for Node-hosted apps, but this product
  needs a long-running media worker and FFmpeg orchestration. ASP.NET Core should
  own the service process; Svelte can compile to static assets served by Kestrel.
- Svelte 5 still works fully in the UI without a separate Node server in
  production.

### Media tools

- FFmpeg for transcoding.
- ffprobe JSON output for media inspection and stream comparison.
- Optional future support for HandBrakeCLI presets, but not in MVP.

Verification should use:

- `ffprobe -v error -print_format json -show_format -show_streams`.
- Decode health check with FFmpeg using `-v error -f null -`.
- Duration tolerance checks.
- Video/audio/subtitle stream policy checks.
- Opt-in VMAF for video re-encodes (off by default and configured per library), with default-on SSIM
  for still-image re-encodes.

### Container shape

Follow the familiar self-hosted media app pattern:

```yaml
services:
  optimisarr:
    image: ghcr.io/jellman86/optimisarr:dev
    container_name: optimisarr
    restart: unless-stopped
    ports:
      - "8787:8787"
    environment:
      - PUID=1000
      - PGID=1000
      - UMASK=002
      - TZ=Europe/London
      - OPTIMISARR__LOG_LEVEL=info
    volumes:
      - /path/to/config:/config
      - /path/to/data:/data
      - /path/to/work:/work
      - /path/to/trash:/trash
```

Use `/data` as the preferred media root so users can mount one consistent path
for movies, TV, and downloads-style layouts. This follows the same operational
lesson as the Servarr/TRaSH Docker guidance: consistent paths avoid needless
copy/delete behaviour and preserve the ability to use atomic moves where the
filesystem allows it.

### Permissions

Support LinuxServer-style environment variables:

- `PUID`
- `PGID`
- `UMASK`
- `TZ`

The container should run the app as the requested UID/GID after startup. Outputs,
database files, temp files, and quarantined originals should be created with the
configured ownership and umask.

### GPU support

Supported targets:

- NVIDIA NVENC/NVDEC through Docker GPU reservations and NVIDIA Container
  Toolkit.
- Intel QSV/VAAPI through `/dev/dri` device mapping.
- AMD VAAPI through `/dev/dri` device mapping.

Compose examples should include:

```yaml
deploy:
  resources:
    reservations:
      devices:
        - driver: nvidia
          count: 1
          capabilities: [gpu]
```

and:

```yaml
devices:
  - /dev/dri:/dev/dri
```

The app should detect encoders at startup with commands such as:

- `ffmpeg -hide_banner -encoders`
- `ffmpeg -hide_banner -hwaccels`
- NVIDIA runtime presence via `nvidia-smi` when available.
- VAAPI/QSV device presence under `/dev/dri`.

Users should be able to pick:

- Auto.
- CPU x264/x265.
- NVIDIA h264/hevc/av1 when supported.
- Intel QSV h264/hevc/av1 when supported.
- VAAPI h264/hevc/av1 when supported.

#### Hardware decoding

When a hardware encoder is selected, the source is also decoded on the GPU
(`-hwaccel` + matching `-hwaccel_output_format`), keeping frames on the GPU end to
end instead of decoding in software and uploading. This is on by default
(`queue.hardwareDecode`) and only applies to a hardware encoder. It is skipped for
HDR→SDR tone-map jobs, whose tone-map filter runs in software and needs frames in
system memory. Because not every source codec/profile can be hardware-decoded, a
decode-setup failure is retried once with the software-decode command rather than
failing the job.

#### Live resource metrics

The Queue view shows a live CPU/GPU usage graph while a job encodes, pushed over
SignalR. All sampling is **unprivileged** so it needs no extra container capability:
CPU from `/proc/stat`; GPU from the per-process DRM fdinfo of Optimisarr's own
ffmpeg child (vendor-neutral — Intel `i915`/`xe` and AMD `amdgpu` expose
`drm-engine-*` busy counters), falling back to the AMD `gpu_busy_percent` sysfs node
or an `nvidia-smi` query. `intel_gpu_top`/the i915 perf interface is deliberately
**not** used because it requires elevation. Where no unprivileged source applies the
UI reports the GPU as unavailable.

## Safety model

### Candidate decision

A file should only enter the queue when it passes all configured gates:

- Extension/container is supported.
- File is not modified within the configured settling period.
- File is not already optimised by Optimisarr metadata.
- Expected saving exceeds threshold.
- Codec/container/quality rules say optimisation is useful.
- Library rule does not exclude path, resolution, HDR, codec, or size.
- The file is not on the exclusion list — either added manually (e.g. from a stuck
  job on the Queue) or automatically after repeated failures. Exclusions are
  durable (keyed by path) and reversible from the library's **Excluded** tab.

### Output verification

Before replacement, all required checks must pass:

- Output exists and is non-empty.
- ffprobe can parse it.
- FFmpeg full decode returns success.
- Duration delta is within tolerance.
- Required video stream exists.
- Required audio streams are present or intentionally converted.
- Required subtitle streams are present or intentionally converted.
- Output size is below original by the configured threshold unless the rule is
  explicitly quality-normalisation rather than size-saving.

Preview jobs reuse the same verification path but never enter replacement. For
long video previews, the worker encodes a 60-second segment from the middle of
the source and the verifier creates a temporary clipped reference from that same
window before running the usual checks. The UI labels those scores as
segment-only; full queue jobs always verify against the complete original.

### Replacement

Preferred replacement sequence:

1. Create output in `/work`.
2. Move original to `/trash/<job-id>/original`.
3. Move verified output into original path.
4. Probe final path.
5. Mark job complete.

If the filesystem cannot perform atomic moves across mounts, the app must fall
back to copy plus checksum/probe verification and clearly report that the library
layout is suboptimal.

## Initial milestones

1. Repository scaffold:
   - backend package
   - Svelte 5 UI package
   - Dockerfile
   - compose example
   - CI
2. Media probe API:
   - scan a configured path
   - store files and stream metadata
   - show candidates in UI
3. Queue and worker:
   - enqueue one file
   - run FFmpeg
   - stream progress to UI
4. Verification:
   - ffprobe before/after comparison
   - FFmpeg decode health check
   - no-delete replacement simulation
5. Safe replacement:
   - quarantine original
   - replace output
   - rollback command
6. GPU:
   - capability detection
   - NVENC compose example
   - `/dev/dri` compose example

## Source notes

- Docker Compose supports GPU device reservations with required capabilities:
  https://docs.docker.com/compose/how-tos/gpu-support/
- Docker Compose deploy devices are defined in the Compose Deploy Specification:
  https://docs.docker.com/reference/compose-file/deploy/
- NVIDIA Container Toolkit is the standard way to expose NVIDIA GPUs to Docker:
  https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/latest/
- LinuxServer documents the familiar `PUID`/`PGID` model used by many self-hosted
  media containers:
  https://docs.linuxserver.io/general/understanding-puid-and-pgid/
- Servarr Docker guidance recommends consistent paths so apps see one filesystem
  layout and can avoid unnecessary copy/delete behaviour:
  https://wiki.servarr.com/docker-guide
- TRaSH Guides explains hardlinks and atomic moves in Docker media stacks:
  https://trash-guides.info/File-and-Folder-Structure/Hardlinks-and-Instant-Moves/
- SvelteKit's Node adapter creates standalone Node servers, but Optimisarr will
  use Svelte as a static UI served by the backend:
  https://svelte.dev/docs/kit/adapter-node
- ASP.NET Core supports hosted background services for long-running tasks:
  https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services
- ASP.NET Core SignalR supports real-time server-to-client updates:
  https://dotnet.microsoft.com/en-us/apps/aspnet/signalr
- Microsoft documents ASP.NET Core Docker image deployment:
  https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/docker/building-net-docker-images
- EF Core supports SQLite as a database provider:
  https://learn.microsoft.com/en-us/ef/core/providers/
- FFmpeg is the universal media converter and ffprobe provides machine-readable
  media stream inspection:
  https://www.ffmpeg.org/ffmpeg.html
  https://ffmpeg.org/ffprobe.html
