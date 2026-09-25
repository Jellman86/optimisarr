# Windows and NVIDIA container acceptance — 2026-09-17

The [media acceptance harness](../../development/media-acceptance.md) completed on the Windows
test PC after it was powered on. **All 146 fleet cases and all 17 container smoke cases passed**;
there were no failed or blocked cases. The Windows core suite also passed all 170 tests on Windows.

## Environment and provenance

| Item | Observed value |
|---|---|
| Host | Windows 11 Pro, build 26200 |
| GPU | NVIDIA GeForce RTX 4070, 12,282 MiB |
| NVIDIA driver | 616.92 |
| Windows SDK | .NET 10.0.401 |
| Native Windows FFmpeg | `n8.1.2-52-g5a03dfa0f6-20260914` |
| Linux kernel | `6.6.114.1-microsoft-standard-WSL2` |
| Docker Engine | 29.8.0 |
| Container encode/probe tools | Jellyfin FFmpeg 7.1.4 |
| Independent container VMAF tool | Static FFmpeg 9.0.1 |
| Source | Working snapshot of `feat/media-acceptance-harness`, based on `81b5a6f` |
| Tested image ID | `sha256:d0722d81fe06e8eb02f1b83d7680d7afcae73a5e7dd41fc14527182a470c75a5` |

This was an uncommitted source snapshot, not a published release. The retained bundle includes
the exact source archive and its SHA-256, full image inspection, platform details and reports.
The Windows solution build succeeded with one existing CA1416 warning in
`ServiceLaunchCommandTests`; the new acceptance executable built successfully.

## Matrix

Both the real container and the real Windows sidecar core exercised all six proved encoders:

- CPU: `libx264`, `libx265`, `libsvtav1`.
- NVIDIA: `h264_nvenc`, `hevc_nvenc`, `av1_nvenc`.

The Windows half used six disposable identities on **one physical PC**, one proved encoder per
identity. Each completed SDR, variable-frame-rate, nonzero-timestamp and 10-bit scenarios plus
three Big Buck Bunny and three Tears of Steel excerpts. H.264's 10-bit cases verified the expected
eligibility refusal; they do not claim a 10-bit H.264 encode. Each Windows encoder also completed
adaptive VMAF selection, and local adaptive selection passed.

Successful candidates passed independent full decode, picture-count/cadence and structure checks,
raw-frame VMAF scoring, application/reference score agreement, unchanged copied audio samples,
worker/encoder attribution, replacement and byte-for-byte rollback. The gates stayed at harmonic
mean 93, fifth percentile 80 and minimum 50; aggregate comparison tolerance remained 0.25 points.

Audio/image conversion, previews, calibration, concurrent isolation, cancellation, low-space
refusal, malformed/truncated/stale uploads and verifier damage detection also passed. The separate
17-case smoke run included an abrupt container restart followed by verified recovery and rollback.

## Evidence and cleanup

The retained HTML, JSON and JUnit reports contain 146/146 and 17/17 passing records respectively.
Per-case evidence includes probes, raw VMAF frames, exact measurement commands, application job
reports and hashes. Worker/server logs and 240 native GPU telemetry samples were retained.
The samples reached 11% encoder and 4% decoder utilization, but concurrent host activity means
those aggregate samples alone do not certify a particular job's decode path.

The SSH transport disconnected near completion. A fresh connection verified the completed report,
the final settings-restoration case, absence of disposable worker processes and removal of the test
containers. The installed Windows sidecar remained running. No production pairing, media library,
checkout or deployed container was modified. Fixture directories and evidence remain available.

## Limits and previous failures

Both Windows and container runs used **CPU libvmaf scoring**. NVENC encoding passed; this is not
CUDA VMAF certification. The normalized film corpus and generated FFV1 sources do not replace a
compressed-source hardware-decode/fallback matrix, HDR/Dolby Vision verification or a long soak.
Intel QSV was subsequently exercised in the [installed Mac/container run](2026-09-17-installed-mac-container.md).
VAAPI and the remaining coverage listed in the harness guide still need their own runs.

Calibration, AV1 and 10-bit HEVC passed in the container, and Windows AV1/10-bit paths also passed.
The earlier failures with the installed Mac bundle remain platform-specific outstanding issues:
its x265 encoder lists only 8-bit pixel formats, and its build includes AV1 hardware decode without
a bundled software AV1 decoder. No quality gate was weakened or failure reclassified to obtain
the passing PC results.

## Retry regression follow-up

The live Intel run exposed a missing output directory when verification triggers a software-decode
retry. A new compressed-source hardware-decode rejection scenario reproduced the same failure
with HEVC NVENC in the original test image. After the fix, the rebuilt image completed all 18
checks, including recorded software-decode retry, final VMAF rejection, unchanged source,
replacement/rollback checks for positive cases, and abrupt-restart recovery.

The fixed image ID is
`sha256:d3aebb26a7e19376f062a3ab5d546480dca1f41a2f1b657c251a63223d86cb84`.
The first reproduction used an x264 lossless input; the final scenario uses a conventional High
profile H.264 source. Both reproduced the old failure and passed with the fix. The High-profile
before-fix run also failed its concurrency coverage assertion because the short jobs did not
overlap. That is retained as a failed coverage attempt, not an application isolation failure.
The concurrency scenario now uses two distinct 120/121-second sources at the slow preset and
still requires actual overlapping job timestamps. No assertion or quality threshold was removed.

These are additional focused runs, not a repeat of the entire 146-case fleet on the rebuilt image.
Raw before/after reports, server logs, image ID and the exact external runner are retained.
The final backend suite passed 2,031 tests and the release build had zero warnings or errors.
