# Linux sidecar and GPU memory validation — 2026-09-27

## Scope

The Linux preview uses the cross-platform Windows sidecar core, with a headless
.NET host, private persistent pairing, exclusive config/scratch directories,
SIGTERM cancellation, fresh capacity reporting and health checks. The separate
container target shares Jellyfin FFmpeg and the pinned VMAF binary with the server.

Native acceptance ran on Quark: Fedora 44, x86-64, Intel Core Ultra 5 235T,
`/dev/dri/renderD128`, .NET SDK 10.0.300. Tests used disposable server instances,
worker identities and generated media. Production libraries and containers were
not changed. Portable FFmpeg 7.0.2 and Intel driver/runtime files were copied from
the existing Frigate container into an isolated directory for native execution.
These are **not** the final image's Jellyfin/pinned VMAF binaries.

## Results

| Check | Result |
| --- | --- |
| Linux release build on Quark | Zero warnings; 11 host tests passed |
| Shared Windows/Linux worker suite | 228 passed |
| Backend suite | 2,296 passed |
| Browser end-to-end suite | 182 passed |
| Python acceptance/release tests | 36 passed |
| Linux final-image CI smoke | Real software encode and CPU VMAF probes passed on the first PR head |
| Quark RAM-backed full application acceptance | 26/26 cases passed |
| Focused remote GPU-surface jobs on tmpfs | HEVC and AV1 through QSV and VAAPI: all four passed |
| QSV/VAAPI HEVC worker jobs | Fixed, adaptive, quality rejection and audio-gate cases passed with strict worker verification |
| Exit cleanup | No job directories or health markers remained after workers stopped |
| Pairing persistence | Credentials were written with Unix mode 0600 |

The RAM run used a new directory on `/dev/shm` (confirmed tmpfs). Source and
candidate copies were written there by the worker. The harness independently
measured results, exercised verified replacement and rollback of **test media**,
and checked that deliberately bad results were rejected. It also exercised
cancelled jobs, low-space refusal, concurrent jobs and protocol faults. Evidence
was copied to disk after the run so it survives a reboot.

The first run on disk had one environment failure: the disposable native server
lacked ExifTool, so its image metadata gate rejected the image fixture. Supplying
ExifTool in the test directory fixed the repeat; no gate was relaxed. All eight
QSV/VAAPI worker scenarios also passed in that first run.

## A GPU frame-pool failure found and fixed

The server's remote decoder selector originally matched only VideoToolbox. It
now also matches proved QSV, VAAPI and CUDA decoders to their encoder family.
These new paths initially accept H.264 eight-bit 4:2:0 sources, matching the
worker's actual probe; other formats retain software decode. Windows workers
must negotiate protocol 3 because older validators reject the surface
arguments. An initial release-number gate was replaced after validation showed
it prevented capable development builds from using GPU decode. The new Linux host supports them from its first preview. Existing
crop/downscale/sample restrictions and verification gates remain in effect.

Direct tests used commands from `FfmpegCommandBuilder`, checked by the shared
worker validator, with hardware decode and GPU output surfaces enabled. The
source contained 100 H.264 frames. Before the fix, one run produced:

| Encoder | Before: decoded output frames | With fix |
| --- | ---: | ---: |
| HEVC QSV | 29 | 100 |
| HEVC VAAPI | 81 | 100 |
| AV1 QSV | 27 | 100 |
| AV1 VAAPI | 84 | 100 |

Counts varied between runs (the first HEVC QSV run kept 40 frames). FFmpeg logged
exhaustion of a fixed hardware frame pool and decoding errors, but exited zero.
This is why a successful process exit alone was inadequate evidence.

The builder now adds `-extra_hw_frames 16` to Intel hardware-decode commands and
`-xerror` so decoding errors fail the encode. The worker accepts only the fixed
pool value. The server's software-decode fallback recognises pool-exhaustion
errors. FFmpeg documents that applications retaining hardware frames may need
[additional pool capacity](https://www.ffmpeg.org/doxygen/8.0/structAVCodecContext.html).

All four corrected commands retained 100/100 frames and passed a complete
software decode with errors treated as fatal. These direct GPU-surface checks
are separate from the 26-case application run, which used software decode and
Intel hardware encode.

A subsequent focused application run paired four disposable workers on Quark.
HEVC QSV, HEVC VAAPI, AV1 QSV and AV1 VAAPI each completed a real remote job on
tmpfs, passed strict worker verification, independent VMAF measurement, size
saving, replacement and rollback. Each recorded command included GPU output
surfaces, the extra frame pool and fatal decoding errors. Verification was on
the worker and none used a software-decode retry. This six-case run (preflight,
four jobs, restore) passed completely. It used a high-quality H.264 source so
each encoder could also satisfy the unchanged size-saving gate. An earlier
attempt correctly refused two candidates that were larger than their source.

The regression tests cover server assignment routing, old Windows worker
compatibility, source-format restrictions, command generation and worker
acceptance for these options. The fixes live in the main container's shared
command builder and the shared Windows/Linux worker core.

## Remaining acceptance before production rollout

- Deploy the published final image through the host's Git-backed stack manager
  and repeat Intel GPU acceptance with its exact Jellyfin/VMAF binaries.
- Extend coverage beyond short SDR fixtures: long jobs, 4K, ten-bit/HDR, more
  concurrency, and RAM exhaustion during an active encode.
- NVIDIA container acceptance belongs on PICARD; Quark cannot establish it.
- Native/systemd packaging remains future work under #293.

Linux RAM work is a bounded operator-mounted tmpfs. It has no automatic disk
fallback or the Mac app's shared RAM reservation manager. GPU surface storage
is separate from source/candidate file storage, and neither removes FFmpeg's
other memory requirements.

## Linux web monitor acceptance

The dedicated Svelte dashboard shares the main app's theme and brand assets. It
shows current media, small preview frames, source codec/resolution/duration/audio,
output encoder/container, GPU decode selection, encoding progress, RAM/disk working
capacity, host CPU and worker GPU utilization. Worker administration stays on the
main server. Optional source metadata is backwards compatible with older
workers. No server paths or credentials are included.

The main server and Linux host share the same unprivileged CPU/DRM/AMD/NVIDIA
sampler. Regression coverage excludes DRM engine capacity from busy-time counters
and verifies progress delivery during short Windows/Linux encodes. Missing
telemetry is distinct from an idle reading. GPU readings require consecutive
samples from a live GPU process.

On Quark, an isolated 120-second H.264 source was encoded with QSV in RAM, fully
verified by the worker, checked independently, and rolled back. The monitor
captured source metadata and preview frames; 188 samples recorded CPU reaching
83%. The fast encode ended between useful GPU samples. A second isolated job
paced source reading with FFmpeg `-re` to span multiple sample intervals; all
three acceptance checks passed, 133 monitor samples captured CPU reaching 54%
and GPU engine utilization reaching 3.7%, with metadata and preview frames.
The pacing wrapper was confined to the private test harness and is not shipped.
No production media or services were modified by these tests.
