# Intel QSV sampled-VMAF seek-alignment validation — 2026-08-11

## Scope and safety

This validation investigated false catastrophic VMAF frames from normal Intel QSV jobs on Riker.
It used retained failed outputs and the original read-only media paths. The queue was suspended with
Optimisarr's supported pause endpoint during the measurements and resumed immediately afterwards.
No original, work output, container, image, database row, or deployment setting was changed.

Media paths and titles are intentionally omitted. The fixture was a 1920×1080 SDR H.264 source and
its 1920×1080 HEVC QSV output. Both contained 16,017 frames and had equal 668.042-second video
durations. Structural, decode, timestamp, duration, audio, and colour verification had passed.

## Environment

| Item | Value |
| --- | --- |
| Host | Riker, Intel N100, i915 |
| Container OS | Ubuntu 24.04.4 LTS |
| Docker | 28.3.1 |
| Application | Optimisarr 0.2.10.0, commit `6562578` |
| Baseline image ID | `sha256:7a0200269c3d79d69b813c234c362e2066c3b94180e4508db0f4cbbbbf954479` |
| Baseline registry digest | `sha256:070d843ad05f4103be76e16458bbb87a31d089df90cace120cecbf738313dabe` |
| Encode / score path | `hevc_qsv`; software confirmation through bundled `ffmpeg-vmaf` / `libvmaf` |
| Model / cadence | `vmaf_v0.6.1`; source cadence 23.976024275107104 fps |

## Reproduction

The production verifier measured three deterministic 40-second windows. Early and late windows
were healthy, but the middle window reliably reproduced the stored failure:

| Window | Frames | VMAF mean | Harmonic mean | Minimum | Frames below 40 | Zero frames |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Early | 959 | 97.217589 | 97.199418 | 93.535696 | 0 | 0 |
| Middle, production graph | 959 | 26.601766 | 1.058156 | 0 | 628 | 444 |
| Late | 959 | 97.356686 | 97.345267 | 95.560051 | 0 | 0 |

The source/output frame timestamps after seeking differed by at most nine microseconds, excluding
duration drift. Inspection before Optimisarr's first `setpts` found the actual cause: for the same
309-second pre-roll seek, the source's first retained decoded picture had PTS 1.351700 while the
output's had PTS 0.017042. The production graph independently applied `PTS-STARTPTS` before cadence
alignment, erased that 1.334658-second presentation offset, and fed unrelated pictures to VMAF.

A fast SSIM cross-check reported 0.759770 for the independently rebased graph. Moving either stream
by one frame did not materially improve it, ruling out a simple adjacent-frame error.

## Corrected graph validation

The corrected graph preserves post-seek PTS through `fps=start_time=0`, then trims the common
five-second pre-roll and resets both aligned timelines for `libvmaf`:

```text
settb=AVTB,
fps=fps=<source cadence>:start_time=0,
trim=start=5:duration=40,
settb=AVTB,setpts=PTS-STARTPTS
```

On the same live image, FFmpeg build, physical host, source, output, seek, cadence, and model, the
corrected middle window produced:

| Frames | VMAF mean | Harmonic mean | Minimum | Frames below 40 | Zero frames | SSIM All |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 959 | 96.917074 | 96.909556 | 94.510016 | 0 | 0 | 0.996669 |

Full-file comparisons continue to reset independent container origins before cadence alignment;
only inputs that actually seek into a shared presentation timeline retain their seek-relative PTS.

## Post-deployment validation

PR [#62](https://github.com/Jellman86/optimisarr/pull/62) passed the backend, frontend,
documentation, secret-scan, CodeQL, Docker build, and final-image smoke gates before merge. The
merged `dev` workflow published revision `09fd9d3` and registry digest
`sha256:a93864b22684a16bcd09af1e12d46e18fdd902a5d20e94e5f29a973c0b87f585`. Dockhand synced the
Git-managed `media_related_stack` at commit `368712b` with `repullImages: true`, returned HTTP 200
for its single deployment request, and started image ID
`sha256:a6ab99107d3c0c69081c910373509e71ce8252c91e0ec91d7543a0850ac77455` healthy.

The exact corrected middle-window command was then repeated with the deployed image and the same
read-only source/output pair. It reproduced all deterministic results above: 959 frames, mean
96.917074, harmonic mean 96.909556, minimum 94.510016, no frame below 40, and no zero frame. The
application-generated one-sided adaptive-quality comparison also retained the seeked reference PTS
while independently rebasing only the unseeked, pre-clipped candidate, confirming the deployed
command-builder branch rather than only the manual graph.

The replacement race was replayed against completed, verified job 4776. The endpoint returned HTTP
200 with the existing purged replacement record 1394; the job still had exactly one replacement
record and no replacement filesystem operation was repeated. A read-only browser check against the
deployed UI confirmed that a fresh video library selects Adaptive VMAF, the visually-lossless
93/80/50 policy, three representative clips, and every-frame scoring without changing an existing
library.

Both health endpoints passed, the application reported version 0.2.10.0 and revision `09fd9d3`, and
the queue resumed from the supported pause API with one active job, no suspended process, and no
failed resume. Post-startup logs contained no application error, exception, or new replacement
warning. The remaining startup warnings were the documented trusted-network authentication notice,
the base-image port-variable override, and the expected low NAS service UID warning; none represented
a regression or a failed runtime dependency.
