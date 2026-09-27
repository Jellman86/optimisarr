# Linux sidecar (preview)

A headless .NET worker using the same transfer, lease, command validation and
verification core as the Windows sidecar. It reads only downloaded job copies;
the server controls verified replacement and rollback.

## Container

Build the `sidecar-runtime` target in the root Dockerfile. The default target
still builds the server. Both targets share Jellyfin FFmpeg and the pinned
libvmaf measurement binary. Published images use
`ghcr.io/jellman86/optimisarr-sidecar:dev`, `:main`, `:latest` and full version
tags. Pull requests build and smoke-test without publishing.

Start with [the Compose example](../../compose.sidecar.example.yml), then open
the dashboard on port 8788 and pair from there: enter the server address and the
code from Settings → Workers → Pair a sidecar. `OPTIMISARR_SERVER` and
`OPTIMISARR_PAIRING_CODE` still pair without the page (for automated or headless
installs). If `OPTIMISARR_SERVER` is set, the page pairs with that server only.
The first connection saves a private credential in `/config/pairing.json`;
preserve that volume across upgrades. A worker revoked on the server returns to
the pairing form. To pair a different server, use a new config volume. Only one process may use a config
directory or scratch directory at a time. Give each worker its own directories.

| Setting | Default / meaning |
| --- | --- |
| `OPTIMISARR_SERVER` | Optional; server HTTP(S) URL. When set, dashboard pairing is limited to it |
| `OPTIMISARR_PAIRING_CODE` | Optional one-time code, tried once at start; the dashboard is the alternative |
| `OPTIMISARR_PAIRING_CODE_FILE` | Alternative mounted secret file; takes precedence |
| `OPTIMISARR_WORKER_NAME` | Hostname |
| `OPTIMISARR_CONCURRENCY` | `1`, range 1–4 |
| `OPTIMISARR_CONFIG_DIR` | `/config`, persistent pairing and health state |
| `OPTIMISARR_SIDECAR_WORK` | `/work`, disposable job storage |
| `OPTIMISARR_FFMPEG` | `/usr/lib/jellyfin-ffmpeg/ffmpeg`; matching ffprobe beside it |
| `OPTIMISARR_FFMPEG_VMAF` | `/usr/local/lib/optimisarr/ffmpeg-vmaf` |
| `OPTIMISARR_ENCODER` | Optional restriction to one successfully probed encoder |
| `PUID`, `PGID` | `1000`; container startup drops privileges |
| `UMASK` | `077` |

No inbound port is needed. Map `/dev/dri` for Intel QSV/VAAPI; the server currently
targets `/dev/dri/renderD128`. NVIDIA requires the host's NVIDIA container runtime
and GPU exposure. Only encoders and decoders that pass real probes are advertised.
GPU decode stays on GPU surfaces for the H.264 8-bit 4:2:0 source format proved
by the decoder probe; other source formats retain software decode for now.
Software filters can require a software decode path. CPU VMAF uses the separate
measurement binary even when encoding runs on the GPU.

`/work` may be a bounded tmpfs for RAM storage. This is an operator-provided
mount; the worker does not create a RAM disk or automatically fall back to disk.
Reserve headroom for FFmpeg and concurrent source/candidate files. Unknown or
insufficient source capacity is refused before downloading; filesystem exhaustion
fails the job and leaves the server's original intact. Linux tmpfs can use swap.

SIGTERM cancels claims and active work, allows the shared session to release its
leases and clean scratch, then exits. Allow at least 45 seconds of container stop
grace. Health requires recent contact with the server. There are no host shutdown
or sleep actions in this container.

## Build and test

```sh
dotnet build sidecars/linux/tests/Optimisarr.Sidecar.Linux.Tests -c Release -warnaserror
dotnet test sidecars/linux/tests/Optimisarr.Sidecar.Linux.Tests -c Release --no-build
dotnet test sidecars/windows/tests/Optimisarr.Sidecar.Core.Tests -c Release
docker build --target sidecar-runtime -t optimisarr-sidecar:test .
bash scripts/ci_linux_sidecar_smoke.sh optimisarr-sidecar:test
```

On managed hosts, image lifecycle and deployments must go through the host's
stack manager. The commands above are for disposable development/CI environments.

For native hardware acceptance, publish the host with `dotnet publish -c Release`
and configure writable scratch/config directories plus both tool paths. `--discover`
prints JSON capabilities without pairing. The
[media acceptance harness](../../docs/development/media-acceptance.md) accepts
`--worker-command '["dotnet","/absolute/path/Optimisarr.Sidecar.Linux.dll"]'`
and `--worker-encoder hevc_qsv` (or `hevc_vaapi`). It creates disposable identities
and test media. Production library paths are never test fixtures.

## Sidecar web dashboard

The container serves a read-only dashboard on port **8788**. It shares the main
app’s Svelte tooling, theme tokens and brand assets, with a dedicated
entry point that ships no server settings or library pages. It shows the worker
connection, current job stages, proved encoders/decoders, CPU/CUDA VMAF support, live host CPU and worker GPU utilization,
and the actual filesystem type and remaining working storage. RAM working files
and GPU frame memory are shown as separate concepts.

The dashboard shows the icon style chosen in the server's Settings, the poster
of each title being worked on, and a notice when the server has a newer release.
Pause, resume, drain, revoke and scheduling stay on the main server. The one
write is `POST /api/sidecar/pair`, accepted only while the worker has no
credential: it takes a JSON body (so a plain cross-site form cannot submit it),
refuses browser requests marked cross-site, and runs one attempt at a time. The
page exposes no pairing credential or raw logs. It shows small media
preview frames only while a viewer is polling; frames are bounded to 8 KiB and
discarded when the job ends. Keep
it on a trusted private network or behind an authenticated reverse proxy; job
titles, preview frames and worker information are visible to anyone who can reach it.

`OPTIMISARR_WEB_ENABLED=true` enables the dashboard (the container default).
Native acceptance runs leave it disabled unless explicitly enabled. Set
`ASPNETCORE_URLS` to change its listen address. `/api/health` is HTTP liveness;
the container healthcheck still requires a recent successful server check-in.

Build the dedicated UI with `cd web && npm run build:sidecar`. The main server
build remains `npm run build`. Test both with the existing `npm run test:e2e`.

Source media facts (codec, resolution, duration, audio and pixel format) are
supplied by the main server as optional display metadata. Older servers still
work but cannot provide those extra facts. Encoding percentage uses the known
source duration; receiving, quality checks and returning the candidate remain
separate stages. CPU is host-wide. Intel/AMD DRM GPU readings cover this worker's
child processes, with device-wide AMD/NVIDIA counters as fallbacks; unavailable
readings remain blank. The same sampler supplies the main container's telemetry.
