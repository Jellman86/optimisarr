# Strict sidecar verification review — 2026-09-17

This follows the earlier [Windows/container matrix](2026-09-17-windows-container-acceptance.md)
and [installed Mac/container investigation](2026-09-17-installed-mac-container.md).
Those runs predate full sidecar verification. They remain useful baseline evidence, not proof of
the new strict path. The [acceptance guide](../../development/media-acceptance.md) describes how
to reproduce the isolated tests and their scope.

## Review findings and hardening

- Existing pairings retained protocol 1 after installing an updated sidecar. Heartbeats now
  renegotiate the supported protocol range; legacy clients remain protocol 1. Strict assignments
  cannot be claimed by an old worker.
- Evidence now rejects failed or incomplete loudness measurements, inconsistent decode results,
  incomplete video probes and missing audio timestamps when the source actually has audio.
  The current library policy is checked again when applying the report.
- The first evidence report is stored with an atomic database compare-and-set. A concurrent
  conflicting report cannot replace it; identical retries remain idempotent. A concurrent HTTP
  regression test submits eight different reports and requires exactly one winner.
- Mac full decoding stops on real decode errors and distinguishes harmless null-muxer timestamp
  notes from corruption. Decode checks cover all video/audio streams; loudness measures the
  primary audio stream explicitly.
- An impossible worker VMAF gate exposed missing worker attribution on failed jobs. Rejection
  itself was correct and used worker measurements, but the queue lost the worker name. The
  API now preserves that attribution, with a regression test for failed delivered jobs.
- Settings have a browser test proving strict verification is opt-in, saved and restored on
  reload. Migrations are reapplied against a populated database to verify idempotency.

## Automated checks

- Backend release build: zero warnings; 2,045 tests passed.
- Mac sidecar: 193 tests passed and the release acceptance worker built.
- Windows sidecar: 172 tests passed both locally and on Windows; the acceptance executable
  built on Windows with warnings treated as errors. The test project retains its pre-existing
  CA1416 warning in `ServiceLaunchCommandTests`.
- Frontend after integrating current dev: clean type/localization checks, 35 unit tests and
  156 browser tests passed, including the strict setting. Browser tests used a separate preview
  port to avoid reading another worktree's development server.
- Harness: 14 self-tests passed. OpenAPI, documentation links and release metadata passed.

## Final hardware runs

| Isolated run | Result | Worker coverage |
|---|---|---|
| Mac `optimisarr-strict-review-03` | 43 passed, zero failed/blocked | HEVC VideoToolbox: SDR, VFR, timestamp offset, 10-bit, three Big Buck Bunny and three Tears of Steel excerpts |
| Windows `optimisarr-strict-picard-02` | 38 passed, zero failed/blocked | RTX 4070 HEVC and AV1 NVENC: SDR, VFR, timestamp offset and 10-bit |

Each worker also passed adaptive quality selection, deliberate VMAF rejection and opt-in
loudness/true-peak checks. Successful candidates passed independent quality measurement,
replacement and byte-for-byte rollback. Rejected candidates retained the original and created
no replacement history. Strict jobs recorded `verificationLocation: Worker`; server fallback
cannot satisfy the harness assertion. Both runs included a native server's local CPU workflows,
audio/image cases, concurrency, cancellation, transfer faults and deliberately damaged fixtures.

The native server used the rebuilt Mac bundle (`n8.0.3` with dav1d and multilib x265). The Windows
worker used the installed Windows FFmpeg beside a freshly built disposable acceptance executable.
Raw reports, probes, VMAF frame logs and job evidence remain under `/tmp/` in the named run
directories on the test Mac. The Windows worker source and scratch data are isolated under
`C:\OptimisarrAcceptance\20260917-strict-review`. The production worker pairing was not changed.

Earlier review runs retained one Mac and two Windows failures for the missing worker-name
assertion described above. Their VMAF verdicts were already correct; the final reruns verify the
attribution fix too. These results do not certify HDR/Dolby Vision, all physical GPUs or endurance.
The committed final-image CI gate supplies separate container evidence for the PR revision.

## Scope of the offload

Strict mode prevents server media-tool fallback for verification of contracted remote full-file
video jobs. It moves decode-health, probe, timestamp, VMAF and requested audio measurements to
the worker. Tests verify the server can evaluate complete evidence with every media-tool path
deliberately pointing at a nonexistent executable; missing evidence fails instead of falling back.

It does **not** remove every server task. The server still scans and initially probes media,
prepares assignments and filter plans, transfers and hashes files, persists state, evaluates the
policy, replaces candidates and maintains quarantine/rollback. Use **Worker only** placement as
well to avoid local encoding fallback. Audio-only, image, preview and calibration work retain
their existing local paths. The setting applies to newly issued leases and defaults off.

The updated sidecars and Mac media bundle must be distributed separately from merging the
server branch. These tests use disposable workers and do not update production installations.
