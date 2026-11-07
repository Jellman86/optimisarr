# AI super-resolution — experimental

Optimisarr can upscale the video of a library that opts in, with one of two engines:

- [Real-CUGAN](https://github.com/bilibili/ailab/tree/main/Real-CUGAN), using
  [nihui's realcugan-ncnn-vulkan](https://github.com/nihui/realcugan-ncnn-vulkan) (MIT licensed;
  the models are MIT licensed by Bilibili). It is trained for anime and other drawn content.
- [Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN), using
  [realesrgan-ncnn-vulkan](https://github.com/xinntao/Real-ESRGAN-ncnn-vulkan) (MIT licensed; the
  project and its models are BSD-3 licensed). Its general model suits live action.

| Library holds | Choose | Scales |
|---|---|---|
| Live-action film and TV | Real-ESRGAN `x4plus` | 4× |
| Drawn content, where speed matters | Real-ESRGAN `animevideov3` | 2×, 3×, 4× |
| Drawn content, fixed 4× | Real-ESRGAN `x4plus anime` | 4× |
| Drawn content, with denoise control | Real-CUGAN | 2×, 3×, 4× |

A model trained on drawn content can make live action look painted, so check a short file before
committing a library to it.

It is **experimental and off by default**. One switch controls everything: while it is off,
its settings are hidden, nothing runs the engine, and a library that opted in behaves exactly
like any other library. Each library keeps its own choice for when you turn it back on.

This is not a video encoder and never replaces NVENC/QSV/VA-API. It replaces the picture only:
frames are upscaled by the engine and re-encoded into a candidate that then passes the same
verification and safe replacement as any other job.

## What you need

The server image for x86-64 includes both engines, each pinned to a release and checked against
its checksum: Real-CUGAN `20220728` and its three model sets under `/opt/realcugan`, and Real-ESRGAN
`20220424` and its models under `/opt/realesrgan`. The image also includes the Vulkan loader and
Mesa drivers.

- **Intel or AMD GPU:** pass the render device to the container (`/dev/dri`), as for hardware
  encoding. Optimisarr adds its user to the device's group at start-up.
- **NVIDIA GPU:** the container runtime must inject the NVIDIA driver with the `graphics`
  capability (for example `NVIDIA_DRIVER_CAPABILITIES=compute,video,utility,graphics`). Render
  group membership alone is not enough.
- **Other architectures:** no engine is bundled, and the settings report it as not installed.
- **CPU:** Real-CUGAN has a CPU mode. Real-ESRGAN has none, so its jobs need a Vulkan GPU.
- **Remote workers:** super-resolution runs on the server only, never on workers.

Optimisarr does not change your Docker permissions, device mappings or host drivers; the
settings page tells you what it finds.

### How fast it is

Upscaling is slow, and the model matters more than anything else. Time per frame, and what that
means for one minute of 24 fps video:

| Engine and model | Upscale | Intel Arrow Lake integrated GPU | NVIDIA RTX 4070 |
|---|---|---|---|
| Real-CUGAN `mixed` | 1080p, 2× | about 6 s (2.5 hours) | not measured |
| Real-ESRGAN `animevideov3` | 720p, 2× | about 2.5 s (1 hour) | about 1.2 s (30 minutes) |
| Real-ESRGAN `animevideov3` | 720p, 4× | about 3.5 s (1.5 hours) | about 1.3 s (30 minutes) |
| Real-ESRGAN `x4plus anime` | 720p, 4× | about 21 s (8 hours) | about 2.7 s (1 hour) |
| Real-ESRGAN `x4plus` | 720p, 4× | about 66 s (26 hours) | about 7 s (3 hours) |

The Real-ESRGAN figures come from short runs on synthetic frames, with the engine started for each
run, so treat them as a guide. Time grows with the source's size. `x4plus` is impractical on an
integrated GPU for anything but short files. Plan for long jobs, and try a short file first.

## Turn it on

In **Settings → AI super-resolution**, switch on **Enable AI super-resolution (experimental)** and
save. The engine settings then appear, and the status card checks the machine. Choose which engine
to check, and **Test with a real sample run** upscales one small picture: the only check that proves
the Vulkan runtime and device work. For Real-ESRGAN it runs `animevideov3` at 2×, and the status
checks that the files for every model are present.

The preset, upscale factor and denoise level below are Real-CUGAN's, used by a library that has not
chosen Real-ESRGAN. The device applies to both engines.

| Setting | Values | Notes |
|---|---|---|
| Content preset | `mixed` (default) / `pro` / `nose` | Names the model set: `mixed` → `models-se` (2×/3×/4×, every denoise level), `pro` → `models-pro` (2×/3×, denoise −1/0/3), `nose` → `models-nose` (2×, no denoise). |
| Upscale factor | 2 (default) / 3 / 4 | Every frame is upscaled by this factor. |
| Denoise level | −1 to 3 (default 1) | −1 conservative, 0 none, 1–3 strength. A combination the model set does not ship is rejected with a specific error, never substituted. |
| Device | Automatic (default) / CPU / Vulkan GPU 0–3 | Automatic lets the engine pick. Only choose a specific GPU that the sample run has verified. |
| Engine executable / models directory | empty or absolute paths | Empty uses the bundled engine. Set these only to use a different installation. Each engine has its own pair. Real-ESRGAN's models directory must contain a folder named `models`. |

**Restore defaults** resets the engine settings but leaves the switch as it is. Backups keep
Real-CUGAN's preset, scale and denoise level and each library's model choice. They never turn the
engine on or set its device or paths, because those depend on the machine.

## Enable per library

While the feature is on, each library has an **AI super-resolution (experimental)** section in its
video settings. Tick **Super-resolution** for libraries whose files you want upscaled, and choose
its **Engine and model**. A library that has not chosen uses Real-CUGAN with the server's settings.
The job's details in the Queue record the engine, model and scale that made its result.

The two Real-ESRGAN `x4plus` models are fixed 4× networks. Asked for a smaller scale, the engine
exits successfully but crops and misstitches the picture, and no later check can tell, so Optimisarr
refuses those combinations.

When a library opts in, also choose its **Quality check for upscaled results**:

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
