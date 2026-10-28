# AI super-resolution (Real-CUGAN)

Optimisarr can upscale the video of a library that opts in with the
[Real-CUGAN](https://github.com/bilibili/ailab/tree/main/Real-CUGAN) engine, using
[nihui's realcugan-ncnn-vulkan](https://github.com/nihui/realcugan-ncnn-vulkan)
packaging (MIT licensed). The engine is **off by default**, and the per-library
switch is off even when the engine is configured — a library that does not opt in
never changes how its jobs run.

This is not a video encoder and never replaces NVENC/QSV/VA-API. It replaces the
picture only: the frames are upscaled by the engine, and the result is re-encoded
into a candidate that then passes the same verification and safe replacement as
any other job.

## Install the engine

The engine is not part of the Optimisarr image. Install the release that matches
your platform from the
[realcugan-ncnn-vulkan releases](https://github.com/nihui/realcugan-ncnn-vulkan/releases)
and either put the executable on `PATH` or set **Engine executable path** in the
settings. The release ships its model directories (`models-se`, `models-pro`,
`models-nose`) directly beside the binary; if you move them, set **Models
directory** to the folder that contains them.

### Vulkan requirement

The engine runs on Vulkan. The machine needs:

- the Vulkan **loader** library (`libvulkan.so.1` on Linux, `vulkan-1.dll` on
  Windows) — even the CPU path (`-g -1`) loads it;
- a driver with a Vulkan **ICD** for GPU execution. Without an ICD the loader can
  still load, but `auto` has nothing to pick, and a run prints
  `vkCreateInstance failed` and either fails or falls back per the device setting.

In a container, GPU execution additionally needs the device and driver libraries
mapped in (the same `/dev/dri` and driver mounts the hardware-encoder setups use).
Optimisarr does not change your Docker permissions, device mappings, or host
drivers; the settings page tells you what is missing and nothing more.

## Configure

In **Settings → AI super-resolution / Real-CUGAN**:

| Setting | Values | Notes |
|---|---|---|
| Enable | off (default) / on | While off, no job changes behaviour. |
| Content preset | `mixed` (default) / `pro` / `nose` | Names the model directory the run uses: `mixed` → `models-se` (the upstream default set: 2x/3x/4x, every denoise level), `pro` → `models-pro` (2x/3x, denoise −1/0/3), `nose` → `models-nose` (2x, no denoise only). These are the sets the upstream release ships — the names describe the directories, nothing more. |
| Upscale factor | 2 (default) / 3 / 4 | Every frame is upscaled by this factor. |
| Denoise level | −1 (default) / 0 / 1–3 | −1 conservative, 0 no denoise, 1–3 strength. The chosen model set must contain the combination — an unsupported combination is rejected with a specific error, never silently substituted. |
| Device | auto (default) / cpu / `gpu:<index>` | `auto` lets the engine pick; `cpu` forces `-g -1`. Only trust a specific index that a probe has verified. |
| Executable path / models directory | empty or absolute paths | Empty resolves the binary on `PATH` and the models beside it. |

**Save** validates server-side and persists; a restart reads the saved values
back, and anything unreadable falls back per field to the documented defaults
rather than half-configuring the engine. **Restore defaults** puts the initial
configuration back into the form. **Detect environment** re-checks the machine;
**Test with a real sample run** performs a real small probe — the only check that
proves the Vulkan runtime, device and models actually work on this machine.

## Enable per library

In the library editor, tick **Super-resolution (Real-CUGAN)**. Eligible video
jobs of that library then run the pipeline; previews, calibration samples and
non-video jobs never do.

## What a job does

1. Checks the source, engine, model files and device before starting; an
   unusable setup fails the job with a specific reason.
2. Extracts every source frame as a PNG sequence in the job's scratch area.
3. Runs the engine directory-to-directory over those frames.
4. Reassembles the upscaled frames with the source's audio, subtitles and
   attachments (the same target codec and container the library's rules resolve).
5. Hands the candidate to the normal verification — decode health, duration,
   retained streams, VMAF gates — and, when it passes, to the existing safe
   replacement. The original is quarantined and rollback-able as always.

Cancellation, failure cleanup, and progress (extraction → engine → reassembly)
behave like any other job; the frame scratch is removed when the attempt ends.

## Output, and what the pipeline refuses

- At 2×, a 720p source becomes **1440p**. The pipeline does not downscale back —
  the candidate keeps the upscaled size. Getting 1080p out of 720p is not what
  this does: upscaling and scaling back down would spend a lot of compute to end
  near where it started.
- **HDR sources are refused** with a clear error: frame extraction would fold the
  HDR picture into SDR.
- **Variable-frame-rate sources are refused**: reassembling at a constant rate
  would mistime them.
- **Hardware encoders are refused** for the reassembly stage.
- A library whose rules crop, downscale, or cap the frame rate is refused with a clear error:
  the engine upscales every source frame, so those rules either would not apply or would
  contradict the job.
- Audio and subtitles are **copied untouched**; the library's audio re-encode
  rules do not apply to a super-resolution job.

## Troubleshooting

- **"Not installed"** — the executable was not found: fix the path or `PATH`.
- **"Configuration problem"** — the chosen preset/scale/denoise combination has
  no shipped model; the error names the file the engine would load.
- **"The Vulkan loader is not available"** — install the loader library; in a
  container, mount it (and the driver libraries) in.
- **The probe fails with `vkCreateInstance failed`** — no Vulkan ICD/driver is
  reachable. GPU execution needs the driver's device nodes and libraries inside
  the container; `cpu` avoids the GPU but still needs the loader.
- Disk space: PNG frame trees are large — gigabytes for a feature-length file.
  The queue's minimum-free-disk gate applies to the job as a whole.

## Known limits

- The engine, its models and the GPU environment are **not bundled** with
  Optimisarr and are not installed by the documentation you are reading — the
  settings page reports what it finds and nothing more.
- Audio re-encode rules and HDR tone mapping do not apply inside a super-resolution
  job; crops, downscales and frame-rate caps on such a library refuse the job instead.
- Frame extraction renders to 8-bit PNG; the engine itself processes 8-bit input.
