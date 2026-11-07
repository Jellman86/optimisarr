# AI super-resolution (Real-CUGAN) — experimental

Optimisarr can upscale the video of a library that opts in with the
[Real-CUGAN](https://github.com/bilibili/ailab/tree/main/Real-CUGAN) engine, using
[nihui's realcugan-ncnn-vulkan](https://github.com/nihui/realcugan-ncnn-vulkan) (MIT licensed;
the Real-CUGAN models are MIT licensed by Bilibili). Real-CUGAN is trained for anime and
other drawn content.

It is **experimental and off by default**. One switch controls everything: while it is off,
its settings are hidden, nothing runs the engine, and a library that opted in behaves exactly
like any other library. Each library keeps its own choice for when you turn it back on.

This is not a video encoder and never replaces NVENC/QSV/VA-API. It replaces the picture only:
frames are upscaled by the engine and re-encoded into a candidate that then passes the same
verification and safe replacement as any other job.

## What you need

The server image for x86-64 includes the engine and its three model sets under
`/opt/realcugan`, pinned to release `20220728` and checked against its published checksum. The
image also includes the Vulkan loader and Mesa drivers.

- **Intel or AMD GPU:** pass the render device to the container (`/dev/dri`), as for hardware
  encoding. Optimisarr adds its user to the device's group at start-up.
- **NVIDIA GPU:** the container runtime must inject the NVIDIA driver with the `graphics`
  capability (for example `NVIDIA_DRIVER_CAPABILITIES=compute,video,utility,graphics`). Render
  group membership alone is not enough.
- **Other architectures:** no engine is bundled, and the settings report it as not installed.
- **Remote workers:** super-resolution runs on the server only, never on workers.

Optimisarr does not change your Docker permissions, device mappings or host drivers; the
settings page tells you what it finds.

### How fast it is

Upscaling is slow. On an Intel Arrow Lake integrated GPU, 2× from 1080p to 2160p took about
**6 seconds per frame**: roughly 2.5 hours per minute of 24 fps video. A dedicated GPU is much
faster, but plan for long jobs, and try a short file first.

## Turn it on

In **Settings → AI super-resolution / Real-CUGAN**, switch on **Enable AI super-resolution
(experimental)** and save. The engine settings then appear, and the status card checks the
machine. **Test with a real sample run** upscales one small picture: the only check that proves
the Vulkan runtime, device and models work.

| Setting | Values | Notes |
|---|---|---|
| Content preset | `mixed` (default) / `pro` / `nose` | Names the model set: `mixed` → `models-se` (2×/3×/4×, every denoise level), `pro` → `models-pro` (2×/3×, denoise −1/0/3), `nose` → `models-nose` (2×, no denoise). |
| Upscale factor | 2 (default) / 3 / 4 | Every frame is upscaled by this factor. |
| Denoise level | −1 to 3 (default 1) | −1 conservative, 0 none, 1–3 strength. A combination the model set does not ship is rejected with a specific error, never substituted. |
| Device | Automatic (default) / CPU / Vulkan GPU 0–3 | Automatic lets the engine pick. Only choose a specific GPU that the sample run has verified. |
| Engine executable / models directory | empty or absolute paths | Empty uses the bundled engine. Set these only to use a different installation. |

**Restore defaults** resets the engine settings but leaves the switch as it is. Backups keep the
preset, scale, denoise level and device; they never turn the engine on or set its paths, because
those depend on the machine.

## Enable per library

While the feature is on, each library has an **AI super-resolution (experimental)** section in its
video settings. Tick **Super-resolution (Real-CUGAN)** for libraries whose files you want
upscaled. Name the library whatever you like.

When a library opts in, choose its **Quality check for upscaled results**:

| Choice | What it does |
|---|---|
| Catch broken frames (recommended, default) | VMAF is measured and recorded, and only its broken-frame floor applies: the lowest frame must stay above the library's catastrophic floor (40 if none is set). It catches corrupted or glitched output without refusing the intended changes. |
| Full VMAF gate | The library's normal VMAF gates apply. Most upscales will be refused. |
| Off | No VMAF; only the structural checks apply. |

VMAF measures how close a result stays to the original, and an AI upscale changes the picture on
purpose. On real anime, a good Real-CUGAN upscale scored about 72 on VMAF v1 where a plain
re-encode of the same frames scored 97 and an ordinary Lanczos upscale 99. Frame, duration,
track, A/V sync, colour and font checks apply whatever you choose.

**Upscaled results always wait for you to review them** before the original is replaced, even
when the library replaces automatically: no metric can prove an upscale looks better.

Only the library's ordinary video jobs run the pipeline; previews, calibration samples and
non-video jobs never do. Its video jobs run on this server only, whatever the library's placement
says, so a worker can never deliver an ordinary source-sized encode in place of the upscale.

## What a job does

1. Checks the engine, its models and the source before starting. A source the pipeline cannot
   reproduce, or a machine without room for a batch, fails the job with the reason.
2. Decodes the source and upscales it **in batches**: a batch of frames is written to scratch,
   the engine upscales it, the upscaled frames are encoded, and the batch is deleted. Scratch
   therefore stays within one batch (at most 4 GiB, and always above the queue's free-space
   reserve) however long the video is.
3. Encodes the upscaled frames with the library's software encoder and quality, and copies the
   source's audio, subtitles, chapters and metadata. For Matroska it also keeps attachments such
   as subtitle fonts; a candidate missing any of them is not accepted. Audio is copied even if
   the library normally converts it to AAC or Opus. Container selection follows that copied
   audio, falling back to Matroska when needed, including the Matroska ALAC safeguard.
4. Hands the candidate to the normal verification. The size-saving gates are off, because
   upscaling grows the file; VMAF follows the library's quality check; every structural gate
   applies. A passing result waits for your review, then the normal safe replacement quarantines
   the original, which can be rolled back as always. Auto-replace and move-on-complete settings
   never bypass this review or overwrite a destination with an unreviewed upscale.

The settings for an attempt are fixed when it starts; editing them affects later attempts.
Turning the feature off stops new super-resolution work; an attempt already running finishes,
or you can cancel it from the Queue. Pausing the queue suspends the engine like any encode.
Each attempt has its own scratch folder under `/work/super-resolution`, which is cleared when
Optimisarr starts, so a crash never leaves frames behind for a retry to reuse.

## What it refuses

The engine works on 8-bit RGB pictures. 8-bit and **10-bit** 4:2:0 video are supported: a 10-bit
source is dithered into the engine's 8-bit pictures and the result is encoded back to 10-bit. Most
anime is mastered in 8-bit and released in 10-bit for compression, so this keeps what the release
carries; the VMAF gate still judges every result. To avoid losing detail or timing, these sources
are refused before anything runs, and are left to the normal pipeline:

- other layouts and depths, such as 4:2:2, 4:4:4 or 12-bit video;
- non-square pixels (anamorphic video), rotated video, or more than one video stream;
- interlaced video, because the engine would enlarge the combing between fields as if it were detail,
  unless the library deinterlaces it first (see [Interlaced video](configuration.md#interlaced-video));
- HDR and variable-frame-rate video;
- libraries whose rules crop, downscale or cap the frame rate, and hardware encoders.

The RGB round trip uses the source's colour matrix and range, then restores its colour tags.
Untagged HD sources use BT.709 and untagged SD sources use BT.601 for that conversion; explicit
wide-gamut colour is refused. Executable and model paths must be absolute, or empty to use the
bundled engine. Linked scratch directories are refused and existing attempts are never reused.

Audio and subtitles are copied untouched; the library's audio re-encode rules do not apply.

## Troubleshooting

- **Not installed:** no engine was found. Use the x86-64 image, or set the executable path.
- **Configuration problem:** the chosen combination has no model, a model file is missing, or the
  engine is not executable. The status names what is missing.
- **The Vulkan loader is not available:** the image includes it, so this means a custom
  installation without `libvulkan.so.1`.
- **The sample run fails with `vkCreateInstance failed`:** no GPU driver is reachable. Check the
  device mapping, or the NVIDIA `graphics` capability.
- **Not enough working space:** free space on the work volume, or lower the queue's free-space
  reserve.
