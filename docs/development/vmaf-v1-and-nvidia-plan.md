# Practical plan: current VMAF and NVIDIA acceleration

Updated: **1 October 2026**. Status: **SDR implementation merged; all four available machines, MSI and published server/worker images validated**. New builds select v1 for ordinary SDR;
running installations change only when updated. HDR/HFR/conversions and started legacy jobs keep v0.
Evidence: [hardware validation and second-order review](../engineering/hardware-validation/2026-10-01-vmaf-v1.md).
Related: [PR #114 and the contributor's comment](https://github.com/Jellman86/optimisarr/pull/114#issuecomment-5909379935),
the [existing model-study tool](vmaf-model-study.md) and [current shadow-scoring decision](vmaf-shadow-decision.md).

## Direction and scope

Move to the current stable VMAF implementation and v1 SDR models, with practical automated testing
across the container and workers. Investigate NVIDIA acceleration separately so it cannot block
that upgrade. The agent should handle roughly 90% or more of the engineering and validation work.
No observer panel, fixed title-count requirement or new subjective research programme is required.

**Optimisarr never conducted a subjective threshold-calibration study for v0.** Its preset floors
are heuristic product choices. Existing per-title encoder calibration chooses an encode setting
against those floors; it does not establish that the floors are perceptually correct. Applying a
much higher evidence bar to adopting v1 would give the older model unjustified authority.

Netflix says v1's score scale was calibrated to remain broadly consistent with v0's interpretation.
Improved banding and chroma sensitivity can legitimately change individual scores and verdicts.
See [Netflix's explanation](https://netflixtechblog.com/vmaf-v1-good-is-not-good-enough-60d7e4244ea8).

Start with the current numerical presets and preserve custom thresholds. Treat those values as
provisional policy, not scientifically validated quality guarantees. Change a preset only when
concrete evidence justifies it, and explain the change. Do not fit v1 to reproduce old pass rates,
apply a blanket score offset, or make agreement with v0 an upgrade requirement.

## Work the agent owns

| Work | Agent responsibility | User input |
| --- | --- | --- |
| Toolchains and implementation | Inventory, pin/build bundles, update model selection and worker compatibility, write tests. | None routinely. |
| Automated validation | Obtain freely licensed fixtures, run scoring/encode/rejection/rollback tests, investigate anomalies and retain evidence. | None routinely. |
| NVIDIA feasibility | Audit exact build licenses, prototype supported acceleration and benchmark on PICARD. | None routinely. |
| Review and delivery | Second-order review, fixes, CI, release documentation and coordinated update preparation. | Only an unresolved product preference or a genuinely ambiguous visual case. |

If visual judgment is necessary, prepare a few short, ready-to-view comparisons with a precise
question. Do not ask the user to collect media, run tests, recruit observers or review a large
corpus. Automated metrics and agent image inspection do not substitute for human perceptual
judgment, but a human study is not a prerequisite for this engineering upgrade.

## Plan A: adopt VMAF v1

1. **Check and align the scoring bundles.** The latest stable release checked on 1 October is
   [libvmaf 3.2.1](https://github.com/Netflix/vmaf/releases/tag/v3.2.1), with the v1.0.16 model family.
   Recheck at implementation, pin exact releases/checksums and retain matching source/licenses.
   The Mac build already pins 3.2.1. Verify the actual Mac app, Windows MSI, main container and
   Linux sidecar executables by running the required models, rather than trusting version strings.
2. **Use v1 consistently for supported SDR work.** Select `vmaf_v1.0.16_3d0h` for standard SDR and
   `vmaf_v1.0.16_1d5h_2160` for UHD, following
   [Netflix's model guidance](https://github.com/Netflix/vmaf/blob/v3.2.1/resource/doc/models_v1.md).
   Preserve aligned reference-size comparisons; measure at 10-bit precision and supply the
   candidate's actual dimensions/bit depth to CAMBI. Adaptive samples, final verification and
   retries must use the same selected model and preparation. Invalidate incompatible cached
   encoder calibration. Retain an explicit supported legacy path for HDR/HFR or other workflows
   outside the initial SDR change; never silently disable a required gate.
3. **Keep migration small and understandable.** Preserve existing preset numbers, custom settings
   and VMAF-disabled libraries. Record the model/preparation in job plans and results, distinguish
   historical scores, and keep already-running jobs on their original measurement plan. Add only
   the persistence/protocol changes needed to achieve that, with an idempotent migration if
   required. Use existing settings/report surfaces and existing-job legacy compatibility;
   a new quality-policy subsystem or large settings redesign is unnecessary.
4. **Validate with the existing harness.** Use a manageable set of freely licensed clips covering
   HD/UHD, animation, grain, dark gradients, chroma detail, motion, 8/10-bit and scaling. Verify each
   fixture's license/attribution. Reuse existing encoded clips and study evidence where suitable.
   Generate deliberate banding/chroma damage, black/dropped/truncated frames and timing faults as
   automated controls. Check full decode, alignment, matching frame coverage, finite scores,
   adaptive selection, quality rejection, retry behaviour and exact rollback. Existing v0/v1
   differences identify cases to investigate; they are not a threshold-fitting exercise.
5. **Run on available hardware and review the effects.** Test Mac VideoToolbox, PICARD native
   Windows NVENC and its separate Linux GPU-container path, plus available Intel/CPU hosts and
   final server/worker images. Keep unavailable rows explicitly pending. Confirm required scoring
   happens on the assigned sidecar in strict worker mode; incompatible old workers cannot accept
   v1 jobs or silently send verification back to the container. Review retries, queue state,
   storage savings, scoring time, custom thresholds and mixed-version upgrades.
6. **Ship the tested SDR upgrade.** Make v1 the normal supported SDR model in the coordinated
   release, with unchanged numerical thresholds unless a documented finding warrants adjustment.
   Explain that scores/verdicts can change because the model detects different artifacts. Update
   the model/backend labels, relevant help and release notes. Preserve all structural gates and
   quarantine/rollback. Keep already-started legacy jobs stable, without requiring
   every existing library to undergo a manual calibration exercise.

**Completion:** required suites and real-media acceptance pass; all available scoring endpoints
load and run the selected models; unsupported combinations are handled explicitly; no incomplete
measurement can authorize replacement. No large subjective study is a release gate.

## Plan B: practical NVIDIA acceleration

### Check feasibility and licensing first

The released [libvmaf CUDA feature build](https://github.com/Netflix/vmaf/blob/v3.2.1/libvmaf/src/meson.build)
does not provide the complete feature set required by the
[v1 model](https://github.com/Netflix/vmaf/blob/v3.2.1/model/vmaf_v1.0.16/vmaf_v1.0.16_3d0h.json).
The [FFmpeg 8.0.3 bridge](https://github.com/FFmpeg/FFmpeg/blob/n8.0.3/libavfilter/vf_libvmaf.c)
also needs scrutiny of precision and chroma-plane transport. A CUDA filter name alone is not proof
of complete v1 scoring. Do not remove features, downconvert to 8-bit or substitute CUDA v0.

[VMAF's BSD-2-Clause-Patent license](https://github.com/Netflix/vmaf/blob/v3.2.1/LICENSE) permits
redistribution subject to its conditions. NVIDIA dependencies have separate terms. Audit the exact
artifacts against the [CUDA agreement](https://docs.nvidia.com/cuda/eula/index.html) and
[FFmpeg's legal guidance](https://ffmpeg.org/legal.html), retaining notices/source manifests.

The [FFmpeg 8.0.3 configure rules](https://github.com/FFmpeg/FFmpeg/blob/n8.0.3/configure) mark
`cuda_nvcc`, `cuda_sdk` and `libnpp` nonfree, but the CUDA VMAF filter itself does not inherently
require those FFmpeg options; `scale_cuda` can use LLVM. Investigate that minimal build route
and driver loading. The existing blanket claim that CUDA VMAF always needs a non-redistributable
FFmpeg build is too broad. This is a candidate route, not confirmed packaging permission.
Do not publish a non-redistributable artifact.

### Implement the useful supported path

1. Benchmark NVIDIA decode followed by complete **CPU v1 on the same sidecar**, with correct
   precision, range, chroma and timing. Enable only when total job time benefits; report the
   backend honestly. This is the achievable first stage if full GPU v1 remains unavailable.
2. Probe complete GPU/hybrid v1 support in the pinned upstream build. If missing features require
   substantial custom ports, record the blocker and defer that work rather than turn this upgrade
   into a new GPU-metrics project. CPU v1 adoption proceeds independently.
3. Where complete acceleration is available, compare identical raw frames and then the full
   compressed-input graph against CPU v1 on PICARD's RTX 4070, separately on native Windows and
   Linux. Record feature/frame/aggregate differences, GPU/driver/build identity and actual speed.
   Include above-8-bit stride, chroma-only and banding controls. Reuse existing frame-alignment
   regressions. Investigate numerical differences; do not invent per-card quality thresholds.
4. Require equal frame coverage, complete finite evidence and tight numerical agreement established
   before acceptance. Confirm near-threshold or suspect accelerated results on CPU **on the same
   worker**. Recheck capability after relevant software/device changes. Test cancellation, memory
   failure, worker disconnect and simultaneous encoding with bounded verification concurrency.
5. Use the existing automatic CPU fallback where permitted and show its reason. Preserve strict
   sidecar-only placement. A requested v1 check must retain its model and gates through fallback.
   Add new controls only if existing ones cannot express the required behaviour.

**Completion:** an audited, tested accelerated path with measurable end-to-end benefit, or a concise
upstream/licensing blocker with CPU v1 working. No promise of full GPU v1 until its missing
features, parity and package licensing are resolved.

## Delivery and safety

Deliver in three reviewable slices: toolchains/model integration; automated hardware validation,
fixes and reporting; then NVIDIA feasibility/acceleration independently. Write failing tests before
new behaviour or bug fixes, run required repository CI/final-image and installer checks, and perform
a second-order review before merge/release. Keep the existing study as a diagnostic tool; relabel
its `UnsafeAccepts` meaning clearly as accepted legacy failures, not proven visually bad output.

Originals remain protected by all required verification gates. Replacement records the quarantine
and rollback path first; purge removes rollback ability. Strict worker verification moves media
measurement off the server, but orchestration, transfers and replacement still belong there.
Optional server shadow research adds server CPU work and remains explicitly opt-in.

## Implemented policy and compatibility

New SDR jobs with a freshly probed, finite positive cadence below 45 fps use the HD/UHD v1.0.16 models above. The job records its model before
adaptive selection or dispatch. Automatic retries keep it; an explicit operator retry clears the
choice and starts under the current policy. Pre-upgrade candidates and already-selected adaptive
quality remain legacy. This prevents model changes between search, encode and final verification.
The nullable model migration preserves existing rows and is idempotent.

Worker protocol 7 fills fixed CAMBI tokens from its actual candidate probe. Unknown, conflicting or
unsupported bit depth fails closed. Older workers may still claim compatible legacy or ungated
jobs, but cannot claim gated v1 work. Both model probes must produce finite frame evidence before
VMAF capability is advertised. CPU scoring on a sidecar still satisfies strict sidecar-only mode.

The container/Linux static measurement image is pinned by digest to `mwader/static-ffmpeg:9.0.2`,
whose build sources pin libvmaf v3.2.1. Windows pins the checksum-verified 30 September BtbN 8.1
GPL bundle with matching retained dependency source cache. Its VMAF revision is
`86da14d0306a138fd3f01319860b905169746516`, eight commits after v3.2.1, including the
backpressure fix and CAMBI AVX2 optimisations. Mac/container use the exact v3.2.1 release.
**Do not establish source provenance from the score log version:** upstream's v3.2.1 archive
still declares `3.2.0` in Meson; Git builds may emit their abbreviated commit instead.

CUDA v1 remains deferred because upstream lacks CAMBI, SpEED chroma and the newer ADM/motion
extractors in its CUDA feature set. The CPU graph keeps the complete v1 model. No numerical offset,
per-GPU threshold, incomplete feature substitution or nonfree artifact is shipped by this change.
A PICARD prototype of CUDA decode followed by full CPU v1 matched all recorded features and scores
but did not beat CPU decode on its bounded HD fixture. Hybrid scoring is therefore not enabled by
default; this small result is not a general benchmark. See the retained hardware evidence.
The optional server shadow study stays opt-in and retains a legacy/v1 diagnostic pair; only the
job's selected production model controls its configured quality gates.
