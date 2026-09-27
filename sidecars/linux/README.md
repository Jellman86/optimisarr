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

Start with [the Compose example](../../compose.sidecar.example.yml). Generate a
pairing code under Settings → Workers and provide a reachable server URL. The
first connection saves a private credential in `/config/pairing.json`; preserve
that volume across upgrades. Remove the one-time code after pairing. To pair a
different server, use a new config volume. Only one process may use a config
directory at a time.

| Setting | Default / meaning |
| --- | --- |
| `OPTIMISARR_SERVER` | Required on first pairing; server HTTP(S) URL |
| `OPTIMISARR_PAIRING_CODE` | One-time code |
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
GPU decode stays on GPU surfaces when the server generates a compatible command;
software filters can require a software decode path. CPU VMAF uses the separate
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
