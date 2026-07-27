# Hardware validation matrix

This matrix separates code support from evidence collected on a real host. **Implemented** means
the command path and fallback behaviour are covered by automated tests; it does not mean that a
physical GPU has completed an Optimisarr job. **Validated** means a real container completed the
listed path and the evidence was observed outside a mock.

Last reviewed: **2026-07-27**.

| Platform | Encode | Hardware decode | HDR→SDR tone map | VMAF path | Live metrics | Last real-host validation | Evidence and known limits |
|---|---|---|---|---|---|---|---|
| CPU (`libx264`/`libx265`) | Validated in every final-image CI run | Not applicable | Software `zscale`/Hable implemented and unit-tested | Validated: software decode and CPU `libvmaf` | Validated: `/proc/stat` CPU usage | Every CI run | The [container smoke test](../../scripts/ci_container_smoke.sh) performs real transcodes, decode checks, and VMAF comparisons in the built image. It cannot validate a GPU. |
| NVIDIA RTX 4070 / NVENC | Validated | Decoder utilisation confirmed on a physical NVIDIA device for the `dev` NVDEC/CUDA path; automated fallback coverage remains in place | Software path only | Implemented and unit-tested for NVDEC + `libvmaf_cuda`; real-host validation pending | Physical decoder activity observed; the full graph evidence bundle is not retained | 2026-07-24 (NVDEC activity) | An external tester confirmed that decoder activity is now visible where it was absent before ([issue evidence](https://github.com/Jellman86/optimisarr/issues/38#issuecomment-5071443854)). That closes the implementation issue, but the exact image digest, driver, fixture, fallback run, CUDA VMAF result, and full job evidence required by the checklist below were not retained; those broader claims remain pending. |
| Intel N100 / QSV | Validated | Validated | QSV VPP completed a synthetic HDR10/PQ→BT.709 hardware-surface run; full Optimisarr job pending | QSV decode + CPU VMAF is implemented and unit-tested; current real-host revalidation pending | Validated through unprivileged DRM fdinfo | 2026-07-27 (QSV VPP filter) | A 24-frame HDR10/PQ fixture decoded with `hevc_qsv`, ran through `vpp_qsv=tonemap=1`, and produced limited-range BT.709 output on the live Jellyfin FFmpeg/iHD/oneVPL stack. Dispatch freshly confirms PQ metadata and excludes Dolby Vision before using this path. A complete replace-bound job and fallback still need recording. Earlier 4K encoding reduced host CPU use from about 142% to 22%; see the [engineering history](../engineering/history.md#phase-7-gpu-support). |
| Intel VA-API | Synthetic hardware run validated; full Optimisarr job pending | Synthetic hardware run validated; full Optimisarr job pending | VA-API VPP completed a synthetic HDR10/PQ→BT.709 hardware-surface run; full Optimisarr job pending | VA-API decode + CPU VMAF is implemented and unit-tested | Implemented and parser-tested through DRM fdinfo | 2026-07-27 (VA-API VPP filter) | The same 24-frame fixture decoded through VA-API, ran through `tonemap_vaapi`, encoded with `hevc_vaapi`, and produced limited-range BT.709 output in the live container. Dispatch restricts the documented HDR10-only filter to freshly confirmed PQ metadata and excludes Dolby Vision. This is direct VA-API evidence, but a complete replace-bound job and fallback still need recording. |
| AMD VA-API | Implemented and unit-tested | Implemented and unit-tested | Implemented and command-tested; physical filter run pending | VA-API decode + CPU VMAF is implemented and unit-tested | Implemented and parser-tested through DRM fdinfo with sysfs fallback | Pending | This is the highest-priority hardware gap. No AMD GPU model, driver, encode, decode, tone-map, VMAF, or metrics run has been recorded. |

These rows cover only codec and bit-depth combinations supported by the selected hardware encoder.
Optimisarr deliberately skips H.264 output for sources above 8-bit before any listed hardware H.264
path is selected.

## What counts as validation

A row moves from **Implemented** to **Validated** only after all applicable checks below are recorded
for the exact image digest or commit. The Tools encoder probe is necessary, but it is not sufficient.

1. Record the date, Optimisarr commit/image digest, host OS, GPU model, driver, and container runtime.
2. Capture **Settings → Tools** after its real test encode reports the intended encoder available.
3. Complete one normal video job and confirm the Queue reports the intended encoder.
4. With hardware decode enabled, confirm the FFmpeg command uses the intended decode path and the job
   completes. Then exercise a source that forces the documented software-decode fallback.
5. For a supported HDR path, opt into hardware tone mapping, complete an HDR→SDR job, and record the
   Rec.709 output evidence plus a source/driver failure that takes the documented software fallback.
6. Enable a library VMAF tier and complete a like-for-like SDR comparison. Record whether scoring ran
   through CPU VMAF, QSV/VA-API decode plus CPU VMAF, or CUDA VMAF, including any fallback.
7. Capture the live CPU/GPU graph while the job runs and record the metrics source (`nvidia-smi`, DRM
   fdinfo, or AMD sysfs).
8. Keep the non-secret command, relevant logs, verification report, and screenshots under a dated
   `docs/engineering/hardware-validation/` folder, then link that evidence from the row above.

Never include media paths, tokens, API keys, webhook URLs, or other host secrets in evidence. A
failed run is useful evidence too: record the failure and keep the row pending rather than tuning
away a hardware-specific error without a reproducible fixture.

## Automated coverage

The repository continuously checks encoder selection and command construction, portable
encoder-effort mapping for x264/x265, SVT-AV1, NVENC, QSV and VAAPI, encoder-family rate controls,
NVDEC/CUDA, QSV, and VA-API device initialisation, QSV/VA-API tone-map filters, hardware decode and
tone-map fallback classification, VMAF
CUDA/QSV/VA-API graphs, capability parsing, and all three metrics parsers. These tests protect the implemented
contract; this matrix exists because none of them can prove a driver and physical GPU work together.
