# Hardware acceleration

See the maintained [hardware validation matrix](hardware-validation-matrix.md) for the distinction
between automated implementation coverage and paths proven on a physical GPU.

Use **Settings → Tools** after deployment. Optimisarr verifies each available
encoder with a real test encode; a GPU device node alone is not sufficient.

Screenshots in this page use fabricated dummy media created for documentation.
No copyrighted material is used.

![Tools tab showing FFmpeg, ffprobe, hardware acceleration, and encoder availability](../images/optimisarr-settings-tools-dark.png)

The bundled Jellyfin FFmpeg is used for both hardware detection and transcoding, so the
Tools page is the source of truth for what this container can actually encode. A separate
static FFmpeg supplies the optional `libvmaf` quality-measurement filter and appears as its
own Tools entry. A configured `OPTIMISARR_FFMPEG_VMAF_CUDA` binary appears separately and is usable
only when it exposes `libvmaf_cuda`; actual GPU/driver/source compatibility is checked by the first
measurement. Runtime failures fall back to software, and a measured hardware-decoded score below
the selected quality floor is confirmed in software before the output is rejected. The Queue shows
the selected encoder on each job.

## Encoder effort

The per-library **Encoder effort** setting describes intent rather than storing a raw FFmpeg
preset. Optimisarr resolves it after the exact encoder has been selected:

| Effort | x264/x265 | SVT-AV1 | NVIDIA NVENC | Intel QSV | VAAPI |
|---|---|---:|---|---|---|
| Fast | `fast` | `10` | `p2` | `fast` | Driver default |
| Balanced | `medium` | `8` | `p4` | `medium` | Driver default |
| Efficient | `slow` | `6` | `p7` | `slow` | Driver default |

This is particularly important in **Auto** mode, where the encoder depends on the target codec,
proved host capabilities, and source bit depth. Existing libraries, API requests, and imported
backups may retain a former x264/x265 value, NVENC `p1`–`p7`, or SVT-AV1 `0`–`13` preset. That exact
value remains in force on its native encoder and stays visibly labelled as legacy until the operator
chooses a portable effort; another encoder family receives its closest safe equivalent. Any
unrecognised value is rejected before a job can reach FFmpeg.

## Intel and AMD

Map `/dev/dri` and set `RENDER_GID` to the host render-node group:

```bash
stat -c '%g' /dev/dri/renderD128
```

Use [Intel QSV](../../compose.intel-qsv.example.yml) or
[Intel/AMD VA-API](../../compose.vaapi.example.yml). Both map `/dev/dri` and
use `RENDER_GID` for render-node access; select **Intel QSV** or **VA-API** in
Settings after Tools has validated the encoder.

### HDR-to-SDR tone mapping

The per-library **HDR handling** drop-down remains the output policy: only **Tone-map to SDR**
requests a colour conversion. **Settings → General → Queue → HDR tone-map engine** chooses its
machine-specific implementation:

- **Software (compatible)** is the default and uses the established `zscale`/Hable Rec.709 path.
- **Hardware when supported** freshly confirms HDR10/PQ input, then keeps decode, tone mapping,
  and encode on Intel QSV or VA-API surfaces when Hardware decoding is also enabled.

Preview and personal-quality clips retain software decoding to preserve exact frame identity.
VMAF-gated HDR→SDR jobs also retain the software production transform so the reference can receive
the identical colour conversion. HLG, Dolby Vision, and unknown transfer metadata also stay on the
compatible software transform because FFmpeg documents `tonemap_vaapi` as HDR10-only. NVIDIA
currently retains software tone mapping; NVENC may still encode the result. If a supported
Intel/VA-API filter or its driver fails to initialise, Optimisarr deletes the partial work output
and retries once with software decode and tone mapping. The normal decode, HDR-signal, Rec.709
metadata, timing, stream, size, and optional VMAF verification gates still decide whether the
completed output may replace its original.

## NVIDIA

Install NVIDIA Container Toolkit and configure `NVIDIA_VISIBLE_DEVICES=all` and
`NVIDIA_DRIVER_CAPABILITIES=compute,video,utility`. The `video` capability is
required for NVENC and NVDEC. Use the [NVIDIA Compose example](../../compose.nvidia.example.yml)
and select a hardware mode only after Tools reports success.

With **Hardware decoding** enabled (the default), an NVENC transcode uses FFmpeg's
`-hwaccel cuda -hwaccel_output_format cuda` path. FFmpeg selects the compatible NVDEC decoder for
the source codec and keeps the decoded frames in CUDA memory for NVENC; Optimisarr does not force a
codec-specific `*_cuvid` decoder. If the source codec or profile is unsupported, device setup fails,
or the CUDA decode path cannot initialise, the job deletes the partial work output and retries once
with software decoding. HDR-to-SDR work currently uses the software tone-map path with NVENC.

For systems with no GPU, use the [CPU-only Compose example](../../compose.cpu.example.yml).

Hardware decode is used with hardware encoders when possible and retries with
software decode when a source cannot be decoded on the GPU. Eligible SDR VMAF passes use the same
selection: Intel QSV and VA-API can decode both inputs before downloading frames for CPU scoring.
That GPU-to-RAM copy means hardware decode is not guaranteed to be faster; benchmark it on the host.
There is no Intel/AMD/NPU backend for VMAF's feature extractors.

The always-on video bit-depth gate also keeps the H.264 compatibility promise honest. Normal,
preview, and personal-quality work never queues H.264 output for a source above 8-bit; the source is
left untouched with guidance to use HEVC or AV1 instead. A source whose bit depth cannot be proved is
also skipped until it is re-probed. The lower-level encoder checks remain as a defensive backstop:
supported NVENC, Intel QSV, and VA-API H.264 paths cannot preserve a 10-bit source, and Optimisarr
never silently converts one to 8-bit.

NVIDIA is the only full scoring-acceleration path. Supply an FFmpeg build with `libvmaf_cuda`,
FFmpeg NVIDIA codec support, and `scale_cuda` through `OPTIMISARR_FFMPEG_VMAF_CUDA`; Optimisarr then
uses NVDEC and keeps both SDR streams in CUDA memory. HDR remains on the software path so its 10-bit
and tone-map preparation is unchanged. See FFmpeg's official
[`libvmaf_cuda` example](https://ffmpeg.org/ffmpeg-filters.html#libvmaf_005fcuda) and
[hardware-acceleration caveats](https://ffmpeg.org/ffmpeg.html#Advanced-Video-options).

GPU usage graphs require an unprivileged metrics source. Intel/AMD are read from
DRM fdinfo and NVIDIA from `nvidia-smi`; if neither is available, encoding can
still work while the UI reports GPU stats unavailable.

Contributors with a physical NVIDIA system can run the packaged
[NVENC quality comparison](nvenc-quality-comparison.md). It creates a private test folder under the
mapped storage root, leaves all supplied clips unchanged, and produces one anonymous text report for
[issue #37](https://github.com/Jellman86/optimisarr/issues/37). The comparison does not alter normal
Optimisarr encoding settings.
