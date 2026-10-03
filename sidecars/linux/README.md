# Linux sidecar (preview)

A headless .NET worker using the same transfer, lease, command validation and
verification core as the Windows sidecar. It reads only downloaded job copies;
the server controls verified replacement and rollback.

For installation, browser pairing on port 8788, monitoring, settings, upgrades and
troubleshooting, use the [Linux container guide](../../docs/setup/linux-sidecar.md).
The [Compose example](../../compose.sidecar.example.yml) starts with CPU encoding;
Intel device mapping and bounded tmpfs scratch are optional.

## Image and UI build

Build the `sidecar-runtime` target in the root Dockerfile. The default target still
builds the server. Both share Jellyfin FFmpeg and the pinned libvmaf measurement binary.
The [Linux workflow](../../.github/workflows/linux-sidecar.yml) tests and publishes
`ghcr.io/jellman86/optimisarr-sidecar` separately from the server image.

Build the dedicated dashboard with `cd web && npm run build:sidecar`. The main server
build remains `npm run build`. Test both with the existing `npm run test:e2e`.

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

## NVIDIA acceptance

The published Linux container has separate [HEVC NVENC hardware evidence](../../docs/engineering/hardware-validation/2026-09-30-linux-nvenc.md)
on an RTX 4070, including strict worker verification, CUDA decoding, RAM cleanup
and independent output measurements. Native Windows results are separate coverage.

## Audio jobs

Protocol 6 supports standalone AAC, Opus and MP3 jobs using the shared worker core.
Audio requires only its proved audio encoder, not a video encoder or VMAF backend.
Strict audio verification uses the encoding FFmpeg for loudness/true-peak checks.
The dashboard shows a labelled, measured source spectrogram while viewed. See the
[worker guide](../../docs/setup/remote-workers.md#standalone-audio) for placement,
verification, preview limits and cover-art restrictions.

## Decoded-picture verification

Full videos keeping their original frame rate require matching source/candidate decoded-picture
counts. Missing counts, lost pictures or added pictures block replacement. This needs protocol 9
when strict verification is enabled. Counting adds a full decode of each file on the worker; those
counts are reused when sampled VMAF needs frame pairing. With strict verification off, the server
measures the counts. Update the sidecar alongside the server; existing pairing is retained.
Previews and intentional frame-rate conversions keep their existing checks.
