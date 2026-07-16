# Hardware validation matrix

This matrix separates code support from evidence collected on a real host. **Implemented** means
the command path and fallback behaviour are covered by automated tests; it does not mean that a
physical GPU has completed an Optimisarr job. **Validated** means a real container completed the
listed path and the evidence was observed outside a mock.

Last reviewed: **2026-07-15**.

| Platform | Encode | Hardware decode | VMAF path | Live metrics | Last real-host validation | Evidence and known limits |
|---|---|---|---|---|---|---|
| CPU (`libx264`/`libx265`) | Validated in every final-image CI run | Not applicable | Validated: software decode and CPU `libvmaf` | Validated: `/proc/stat` CPU usage | Every CI run | The [container smoke test](../../scripts/ci_container_smoke.sh) performs real transcodes, decode checks, and VMAF comparisons in the built image. It cannot validate a GPU. |
| NVIDIA RTX 4070 / NVENC | Validated | Not implemented for normal transcodes; NVENC currently consumes software-decoded frames | Implemented and unit-tested for NVDEC + `libvmaf_cuda`; real-host validation pending | Implemented and parser-tested through `nvidia-smi`; real-host graph evidence not retained | 2026-06 (encode only) | A manual transcode reported 52–81% encoder utilisation; see the [engineering history](../engineering/history.md#phase-7-gpu-support). The exact driver, fixture, result log, and validation date were not retained, so this must be rerun before claiming current-version coverage. |
| Intel N100 / QSV | Validated | Validated | QSV decode + CPU VMAF is implemented and unit-tested; current real-host revalidation pending | Validated through unprivileged DRM fdinfo | 2026-06 | A 4K manual encode reduced host CPU use from about 142% to 22% with render/video engines active; see the [engineering history](../engineering/history.md#phase-7-gpu-support). The recent sampled-VMAF alignment change still needs a clean host run recorded here. |
| Intel VA-API | Implemented and unit-tested | Implemented and unit-tested | VA-API decode + CPU VMAF is implemented and unit-tested | Implemented and parser-tested through DRM fdinfo | Pending | Shares `/dev/dri` plumbing with QSV, but QSV evidence is not VA-API evidence. Do not mark validated from device detection or a successful encoder probe alone. |
| AMD VA-API | Implemented and unit-tested | Implemented and unit-tested | VA-API decode + CPU VMAF is implemented and unit-tested | Implemented and parser-tested through DRM fdinfo with sysfs fallback | Pending | This is the highest-priority hardware gap. No AMD GPU model, driver, encode, decode, VMAF, or metrics run has been recorded. |

## What counts as validation

A row moves from **Implemented** to **Validated** only after all applicable checks below are recorded
for the exact image digest or commit. The Tools encoder probe is necessary, but it is not sufficient.

1. Record the date, Optimisarr commit/image digest, host OS, GPU model, driver, and container runtime.
2. Capture **Settings → Tools** after its real test encode reports the intended encoder available.
3. Complete one normal video job and confirm the Queue reports the intended encoder.
4. With hardware decode enabled, confirm the FFmpeg command uses the intended decode path and the job
   completes. Then exercise a source that forces the documented software-decode fallback.
5. Enable a library VMAF tier and complete a like-for-like SDR comparison. Record whether scoring ran
   through CPU VMAF, QSV/VA-API decode plus CPU VMAF, or CUDA VMAF, including any fallback.
6. Capture the live CPU/GPU graph while the job runs and record the metrics source (`nvidia-smi`, DRM
   fdinfo, or AMD sysfs).
7. Keep the non-secret command, relevant logs, verification report, and screenshots under a dated
   `docs/engineering/hardware-validation/` folder, then link that evidence from the row above.

Never include media paths, tokens, API keys, webhook URLs, or other host secrets in evidence. A
failed run is useful evidence too: record the failure and keep the row pending rather than tuning
away a hardware-specific error without a reproducible fixture.

## Automated coverage

The repository continuously checks encoder selection and command construction, encoder-family rate
controls, QSV/VA-API device initialisation, hardware-decode fallback classification, VMAF CUDA/QSV/
VA-API graphs, capability parsing, and all three metrics parsers. These tests protect the implemented
contract; this matrix exists because none of them can prove a driver and physical GPU work together.
