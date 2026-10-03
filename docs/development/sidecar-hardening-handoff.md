# Sidecar hardening handoff — 28 September 2026

## Start here

The next work is to **reproduce the QSV timestamp failure, investigate the two late-window
VMAF failures, then add regression coverage**. Do not start by switching VMAF models or releasing
more failed jobs. The [roadmap](../roadmap.md) sets the order; this document supplies the evidence.

Read `AGENTS.md` and `CLAUDE.md` before implementation. Fetch current `dev`, account for local
changes, then use a short-lived branch and a pull request into `dev`. The observations below are
from one installation; job IDs identify its evidence, not portable fixtures.

## Implemented and deployed

- Public release: **0.2.17**, published September 27. Linux is a preview; Windows is unsigned.
- Development baseline: **`23a4e51b1885fe2c43bfb55a38eb5f0704aea3be`**,
  [PR #312](https://github.com/Jellman86/optimisarr/pull/312), deployed September 28 to the main
  container and all three worker platforms. The displayed application version remains 0.2.17;
  identify development deployments by source revision/image digest or signed package provenance.
- Linux shares the Windows worker core and the main container's media toolchain. Its browser
  pairing, media preview, CPU/GPU telemetry, bounded RAM storage and Compose deployment already
  exist. See [Linux setup](../setup/linux-sidecar.md) and [acceptance](media-acceptance.md).
- PR #312 applies current eligibility/exclusions to worker claims, preserves the latest eight
  retry attempts with bounded logs, records per-window VMAF evidence and size-budget diagnostics,
  and samples matching-frame VC-1 files by sequential decoded frame number. It also recovers the
  pinned Windows media tools from a checksum-verified installer when the upstream release expires.
- The merged commit passed main CI (including browser and media acceptance), Linux paired RAM
  acceptance/image publication, Windows installer tests, CodeQL and secret scanning. Mac was
  signed/notarised; native upgrades retained pairing. These are baseline results, not validation
  of a future fix.

## Completed trial

Twenty previously failed jobs were released only after deployment. Each sidecar had one slot;
all three processed work concurrently. Original failure reports were saved and only the selected
jobs' automatic repeated-failure exclusions were removed. Existing verification gates remained.

| Result | Count | Evidence |
| --- | ---: | --- |
| Completed and verified | 10 | Mac 5, Linux 4, Windows 1 |
| Failed | 4 | Two VMAF failures, one size-budget rejection, one local QSV mux failure |
| Awaiting size review | 6 | Sample forecasts exceeded the permitted final size before full encoding |

All workers ended online and idle; the Linux 4 GiB RAM workspace was fully free. No outage or
confirmed stall occurred. The last job's long `Probing` state was an advancing local adaptive
search, not a stalled sidecar. The batch monitor was deleted after the final result. There is
nothing left to poll automatically. Rejected/held originals were not replaced.

## Next investigations and acceptance criteria

### 1. Local QSV timestamp/mux failure — job 5693

Adaptive samples passed, but the full encode failed after three frames with
`Non-monotonic DTS; previous: 0, current: 0` and MP4 mux error `-22`. This ran in the **main
container**, using FFmpeg 7.1.4-Jellyfin, H.264 QSV decode and HEVC QSV encode. The command already
included `-xerror`, `-fflags +genpts` and `-fps_mode passthrough`.

Inspect the source's initial packet and decoded-frame timestamps, then reproduce the full-file
start with isolated hardware/software decode comparisons. Determine whether the duplicate output
DTS originate in the source, decoding, encoder or timestamp conversion. Source corruption has
not been established. Do not remove strict error handling or force CFR merely to get a green job.

Start in:

- `src/Optimisarr.Core/Queue/FfmpegCommandBuilder.cs`
- `src/Optimisarr.Core/Queue/HardwareDecodeFallback.cs`
- `src/Optimisarr.Api/Queue/QueueDispatcher.cs`
- `tests/Optimisarr.Tests/HardwareDecodeFallbackTests.cs`

The current fallback recognises hardware setup failures, not this muxing error. That explains
why no fallback ran; it does not prove that broadening its signature is the right fix.

**Done when:** a test reproduces the actual condition before the fix, isolated real-QSV acceptance
proves correct timestamps/frame counts and safe rejection, and the main/sidecar paths that share
the command builder are covered. Retain the original and all failed candidates as evidence.

### 2. Late-window VMAF collapse — jobs 5687 and 5689

Both failed on the Windows NVENC worker after software-decode fallback. Other verification gates
passed, including decode health, duration, source timeline, colour and size. Per-window harmonic
scores were:

| Job | Early | Middle | Late | Aggregate |
| --- | ---: | ---: | ---: | ---: |
| 5687 | 96.41 | 95.81 | 9.83 | 24.48 |
| 5689 | 95.62 | 96.79 | 15.28 | 34.78 |

Frame alignment is a hypothesis; localised output damage has not been ruled out. These sources
are H.264: the VC-1-only sequential comparison fix does not cover them. Current diagnostic logs
retain both planned comparison commands, hashes and window scores, but do **not** identify the
worker's chosen pairing mode/offset. Do not infer that choice from the planned commands alone.

Use the [retained-file pairing procedure](media-acceptance.md#investigating-frame-pairing-on-retained-files)
and `scripts/diagnose_frame_pairing.py`. Start with the failing late window, compare identical
sequentially decoded frames, and preserve probe output, hashes, commands and raw score logs.
Then check early/middle windows as controls. Keep mismatched frame counts and genuine lost-frame
cases distinct from seek/alignment defects.

**Done when:** a reproducible fixture distinguishes genuine damage from measurement error;
regressions exercise the production comparison graph on the main container and affected workers;
and new diagnostics report the comparison actually selected, with compatible handling of older
workers. Extend the proven fix to shared paths only where the evidence applies.

### 3. Expected policy outcomes and harness coverage

Job **5971** narrowly missed the quality gate on Mac: harmonic 89.61 versus 90, fifth percentile
85.36 and minimum 80.14. Its automatic higher-quality retry switched to NVENC and hit the size
budget: 233,570,348 bytes observed versus 213,905,564 allowed. This final rejection shows the size
guard working; it is not the near-zero late-window pattern above.

Jobs **5845, 5847, 5877, 5878, 5883 and 5935** remain `AwaitingSizeReview`. Samples projected final
sizes of 109.5%–203.2% of the source while missing the quality target. Leave these held unless the
operator explicitly chooses another policy. They are not successful encodes or worker crashes.

Extend the existing [media acceptance harness](media-acceptance.md), rather than creating a
parallel test runner. Add reproducible cases for confirmed defects and assert that quality/size
rejections preserve originals, release leases and clear RAM. Include full-file starts and late
windows, not only short middle clips. Missing hardware stays explicitly blocked with a nonzero
result, while independent available cases continue. Validate real Intel/NVIDIA/VideoToolbox
paths on suitable hosts; CI success alone does not establish hardware coverage.

## Evidence and operating boundaries

On the original workspace, the private handoff is
`.agents/nevercommit/optimisarr-next-agent.md` above the repository. It points to the timestamped
snapshots, full failure records, logs, deployment provenance and host runbooks. If unavailable,
obtain equivalent evidence from the operator before treating installation-specific observations
as reproducible facts. Never commit credentials, private library files or raw operational logs.

Start with `final-summary.md`, `failure-5693-evidence.json`, `failure-5687-evidence.json`,
`failure-5689-evidence.json`, `failure-5971-evidence.json` and the timestamped process logs in
that evidence directory. Verify retained media still exist and match recorded hashes before use.
Some pre-retry process logs were already unavailable; the old verification reports are retained.

The monitoring task authorised read-only investigation, not further retries or gate changes.
Use disposable media/config/scratch for experiments. Production work must preserve verification,
rollback, exclusions and pairing. This estate deploys containers only through Git-backed Dockhand
after successful CI/image publication; follow its workspace runbook. Recheck current versions and
job state before any newly authorised deployment or retry.

Keep VMAF v0 authoritative. Resume the [v1 study](vmaf-model-study.md) under the
[shadow-research decision](vmaf-shadow-decision.md) after measurement correctness is resolved.
No public release beyond 0.2.17 was created by this trial. Any release of follow-up work still
needs the [release checklist](releasing.md), exact-tag evidence and matching native packages.
