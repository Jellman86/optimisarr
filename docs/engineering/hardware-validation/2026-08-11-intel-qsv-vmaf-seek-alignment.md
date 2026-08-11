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

To be completed after the reviewed `dev` image is published and deployed through Dockhand:

- record the new image ID and registry digest;
- repeat the completed-job replacement request and require an idempotent HTTP 200 response;
- run the corrected sampled-VMAF command from the deployed image and require the middle-window
  values above within normal deterministic floating-point tolerance;
- confirm health/readiness, queue resume state, active progress, and absence of new startup errors.
