# Sidecar hardening handoff

Reviewed against development code and diagnostic acceptance on **9 October 2026**.
Read the repository engineering and documentation standards before implementation. Start
from current `dev`, preserve other agents' work, and deliver each fix through a reviewed PR.
This document records evidence and remaining scope; check the latest issue and code before
retrying jobs or changing a deployment.

## Current work

| Issue | Delivered evidence | Remaining scope |
| --- | --- | --- |
| [#354](https://github.com/Jellman86/optimisarr/issues/354), Windows updates | Checked MSI upgrades verify installed files, sustained service/tray health and fresh server check-ins. Regional date parsing is corrected. | Complete live checked-upgrade verification with the logging fix in [#362](https://github.com/Jellman86/optimisarr/pull/362). The MSI remains unsigned; do not imply it is trusted under every Windows policy. |
| [#353](https://github.com/Jellman86/optimisarr/issues/353), DTS-only timing | Initial pictures are retained, decoded counts are required for applicable full video checks, and full-file quality pairing handles repeated timestamps. A generated numbered-picture fixture checks identity, order, audio, subtitles and rollback. | Independent normal-playback evidence for the affected retained real source. Matching counts alone do not prove picture identity or correct timing. |
| [#334](https://github.com/Jellman86/optimisarr/issues/334), test timing | [#360](https://github.com/Jellman86/optimisarr/pull/360) separates process startup from output-size abort latency and checks child cleanup under Windows CPU load. It also removes a race in the concurrent-runner test. | Merge after the exact revision's complete CI and installer checks pass. |
| [#331](https://github.com/Jellman86/optimisarr/issues/331), platform tests | [#361](https://github.com/Jellman86/optimisarr/pull/361) runs the backend suite on native Windows with eight explicitly documented POSIX-only skips. | Closed. Native Windows server hosting is still outside supported deployment scope. |
| [#242](https://github.com/Jellman86/optimisarr/issues/242), opt-in diagnostics | Schema 4 opt-in capture correlates server and native sidecar records, frozen gate/provenance summaries, transfers, scheduling and replacement outcomes; retention, pinning and local exports are available. See [9 October acceptance](../engineering/hardware-validation/2026-10-09-correlated-diagnostics.md). | Release and exact installed-build rollout remain separate from isolated development acceptance. Legacy unrecorded tools/timing and offline local evidence remain explicit. |
| [#332](https://github.com/Jellman86/optimisarr/issues/332), audio/image quality | Opt-in Zimtohrli audio and re-encoded soundtrack reports, explicit gates, codec-aware bitrate controls and strict worker measurement are implemented in development. | SDR image SSIMULACRA2 qualification and integration, broader audio coverage, adaptive bitrate selection and any calibrated default. |

VMAF v1 is released for eligible SDR work. Existing numeric floors are operator policies,
not thresholds calibrated by an Optimisarr listening or viewing study. Use the
[current model decision](vmaf-v1-and-nvidia-plan.md) and
[recorded hardware acceptance](../engineering/hardware-validation/2026-10-01-vmaf-v1.md).
Predicted outputs above the configured size limit now fail before full encoding, with
an explicit prediction reason. Earlier `AwaitingSizeReview` records below describe old
behavior and must not be used as current policy.

For diagnostics, keep collection opt-in, bounded and private. Preserve original/candidate
hashes, actual selected commands and measurement coverage where recorded. Planned commands
are not proof of the worker's chosen comparison. See
[retained-file investigation](media-acceptance.md#investigating-frame-pairing-on-retained-files).

## Historical trial: 28 September 2026

The baseline was `23a4e51b1885fe2c43bfb55a38eb5f0704aea3be`
([#312](https://github.com/Jellman86/optimisarr/pull/312)), after release 0.2.17.
Twenty selected failed jobs were retried through supported controls, with one slot on
each worker. Originals and saved failure reports were retained.

| Result then | Count | Observation |
| --- | ---: | --- |
| Completed and verified | 10 | Mac 5, Linux 4, Windows 1 |
| Failed | 4 | Two VMAF failures, one size-budget rejection and one local QSV mux failure |
| Awaiting size review | 6 | Sample forecasts exceeded the permitted size; this is historical behavior |

The main-container QSV job 5693 failed at the full-file start with duplicate DTS and
MP4 mux error `-22`. It already used strict error handling, generated timestamps and
passthrough cadence. That observation did not establish source corruption. Later
initial-picture and repeated-timestamp work is tracked in #353; do not assume this old
snapshot reproduces the current implementation.

The Windows worker's H.264 jobs 5687 and 5689 passed the other reported gates but had
late-window VMAF collapses:

| Job | Early | Middle | Late | Aggregate |
| --- | ---: | ---: | ---: | ---: |
| 5687 | 96.41 | 95.81 | 9.83 | 24.48 |
| 5689 | 95.62 | 96.79 | 15.28 | 34.78 |

These observations motivated retained-file comparisons. They alone do not distinguish
measurement misalignment from damaged output. Job 5971 was different: harmonic VMAF
89.61 missed its floor of 90, then a higher-quality retry exceeded its size budget
(233,570,348 bytes observed against 213,905,564 allowed). Do not turn an expected policy
rejection into a timestamp defect solely because the job failed.

All workers ended online and idle and the Linux RAM workspace was free. The trial
monitor was removed. These are dated results, not current fleet health assertions.

## Test and deployment boundaries

Extend the existing media acceptance harness. Use disposable configuration and attributed
free or generated media. Prove good outputs pass and deliberately damaged outputs fail;
also check cancellation, reconnect, concurrency, leases, scratch cleanup and exact rollback.
Record decoded picture identity/order separately from packet totals and presentation timing.
The [4 October follow-up](../engineering/hardware-validation/2026-10-04-backlog-hardening.md)
records available-machine results and their limits.

Do not weaken decode, quality, size, timing or replacement checks to make a test pass.
Missing required hardware is an explicit unresolved result. CI success cannot certify a
physical GPU path. Publish only after complete CI, and verify the exact installed revision
and native package on each available host. Apply the operator's deployment runbook.
Never commit credentials, private media, raw operational logs or installation-specific paths.
