# Linux container NVENC acceptance — 2026-09-30

## Scope and isolation

PICARD's RTX 4070 ran the published Linux sidecar container, independently of its
installed Windows service. The worker image was pinned to
`sha256:bd40406bff4b3ce296f04cf8e01ad02ff0abc4102a1c1693e5aa5a73108dc35a`,
with source revision `3afbf10ce1edcd9ccb49eed9acd1a72eb34e4854`. The host used
WSL Ubuntu, Docker 29.8.0 and NVIDIA driver 617.14.

All container deployment and removal used a Git-backed Dockhand test stack.
An outbound Hawser connection carried management traffic through a temporary SSH
forward; no unauthenticated Docker TCP endpoint or Docker CLI lifecycle operation
was used. Read-only Docker inspection, logs and finite processes inside the owned
container supplied capability and working-file observations.

A separate native Linux server used the same reviewed application code, .NET SDK
10.0.401, a new database, test libraries and disposable pairing. Worker configuration and
media lived on tmpfs, with a 4 GiB scratch bound and a 6 GiB container memory
limit. No production jobs, pairing, schedules or verification gates were changed.
Fixtures were generated 16-second, 320×180 videos; they contain no private media.

## Matrix and evidence

The final fleet matrix passed **43/43 cases**, with no failures or blocked cases.
It required strict worker verification and independently
measured every accepted output. It also enabled the explicit server VMAF shadow
comparison on the isolated server. That extra measurement is test work, not a
claim that strict worker mode requires server VMAF in production.

| Area | Passed evidence |
| --- | --- |
| Actual image capabilities | HEVC NVENC encode, CUDA decoder and VMAF probes |
| Video inputs | SDR, variable cadence, stream offset, 10-bit SDR and fractional timestamps |
| Output correctness | Full decode, matching frame counts/cadence, stream retention, unchanged quality gates and smaller output |
| Quality | Independent VMAF recomputed from raw frames and comparison with worker evidence |
| GPU decoding | Compressed H.264 source; delivered command contains CUDA GPU-surface decoding |
| Adaptive encoding | Bounded search followed by a fully verified NVENC candidate |
| Rejection | Deliberately impossible VMAF gate protects the original |
| Timed subtitles | MP4 timed text converted to ASS in Matroska with preserved cues |
| Audio checks | Loudness and clipping gates alongside worker video processing |
| Job lifecycle | Running cancellation, unavailable-worker placement and reconnect after abrupt isolated-server restart |
| RAM and monitor | Source/candidate files observed on tmpfs, fresh utilization samples and working-file cleanup |
| Replacement safety | Verified replacement and byte-identical rollback of test originals |
| Independent controls | Local CPU workflows, image/audio metadata, preview cleanup, concurrency, protocol faults and black/dropped/truncated oracle rejection |

Reports retain per-case plans, verification JSON, independent measurements,
worker monitor samples, GPU/driver identity and the image revision. Production
failure records and originals are outside this disposable run.

The first attempt was not a pass: the management connection could not reach the
worker and the native test server lacked ExifTool. An outbound managed connection
and the missing host dependency corrected the setup. Its failed report remains
separate from the final evidence; no verification gate was weakened.

## Limits

This closes the missing Linux-container HEVC NVENC hardware coverage in #293.
It does not certify AV1 NVENC, every NVIDIA generation, HDR tone mapping,
production-length media, maximum concurrency, driver upgrades or every codec.
The earlier Intel and CPU evidence remains in the
[Linux validation record](2026-09-27-linux-sidecar.md). Native Windows NVENC
results are separate coverage and were not substituted for this container run.
