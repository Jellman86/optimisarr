# Hardware acceleration

Use **Settings → Tools** after deployment. Optimisarr verifies each available
encoder with a real test encode; a GPU device node alone is not sufficient.

## Intel and AMD

Map `/dev/dri` and set `RENDER_GID` to the host render-node group:

```bash
stat -c '%g' /dev/dri/renderD128
```

The Compose example contains the required `devices` and `group_add` settings.

## NVIDIA

Install NVIDIA Container Toolkit and configure `NVIDIA_VISIBLE_DEVICES=all` and
`NVIDIA_DRIVER_CAPABILITIES=compute,video,utility`. The `video` capability is
required for NVENC. Select a hardware mode only after Tools reports success.

Hardware decode is used with hardware encoders when possible and retries with
software decode when a source cannot be decoded on the GPU.
