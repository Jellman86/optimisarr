# Hardware acceleration

Use **Settings → Tools** after deployment. Optimisarr verifies each available
encoder with a real test encode; a GPU device node alone is not sufficient.

Screenshots in this page use fabricated dummy media created for documentation.
No copyrighted material is used.

![Tools tab showing FFmpeg, ffprobe, hardware acceleration, and encoder availability](../images/optimisarr-settings-tools-dark.png)

The bundled Jellyfin FFmpeg is used for both hardware detection and transcoding, so the
Tools page is the source of truth for what this container can actually encode. A separate
static FFmpeg supplies the optional `libvmaf` quality-measurement filter and appears as its
own Tools entry. The Queue shows the selected encoder on each job.

## Intel and AMD

Map `/dev/dri` and set `RENDER_GID` to the host render-node group:

```bash
stat -c '%g' /dev/dri/renderD128
```

Use [Intel QSV](../../compose.intel-qsv.example.yml) or
[Intel/AMD VA-API](../../compose.vaapi.example.yml). Both map `/dev/dri` and
use `RENDER_GID` for render-node access; select **Intel QSV** or **VA-API** in
Settings after Tools has validated the encoder.

## NVIDIA

Install NVIDIA Container Toolkit and configure `NVIDIA_VISIBLE_DEVICES=all` and
`NVIDIA_DRIVER_CAPABILITIES=compute,video,utility`. The `video` capability is
required for NVENC. Use the [NVIDIA Compose example](../../compose.nvidia.example.yml)
and select a hardware mode only after Tools reports success.

For systems with no GPU, use the [CPU-only Compose example](../../compose.cpu.example.yml).

Hardware decode is used with hardware encoders when possible and retries with
software decode when a source cannot be decoded on the GPU.

GPU usage graphs require an unprivileged metrics source. Intel/AMD are read from
DRM fdinfo and NVIDIA from `nvidia-smi`; if neither is available, encoding can
still work while the UI reports GPU stats unavailable.
