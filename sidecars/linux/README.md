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
