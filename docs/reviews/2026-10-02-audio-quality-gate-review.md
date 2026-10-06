# Audio quality gate implementation review

Reviewed on 2 October 2026 against the implementation branched from `23981797`.

## Outcome

Music and mixed libraries can require their own maximum Zimtohrli audio difference before
replacement. The setting is off by default and has no assumed threshold. Every measured channel
in every required sample must be at or below the selected limit. Missing, incomplete or invalid
measurements fail the gate. The existing replacement and quarantine safeguards still apply.

Strict worker verification measures on the worker and returns evidence tied to the source and
candidate. The server validates that evidence and compares channel distances against its saved
job policy. It does not run audio measurement as a fallback. Coordination, evidence processing,
delivery checks and replacement still involve the server.

## Test evidence

- Release build: zero warnings or errors. Backend: 2,660 tests passed.
- Frontend: zero Svelte/TypeScript errors or warnings, all nine locales valid, 75 unit tests passed.
- Playwright: 224 tests passed, including phone and desktop in both themes, keyboard selection,
  custom and zero thresholds, draft preservation, passing and blocked report displays.
- Python script suite: 74 tests passed. Documentation links, API reference, generated OpenAPI,
  release metadata and whitespace checks passed.

The test-first evidence includes rejected missing limits, preview isolation, legacy backup
preservation, horizontal point controls and the gate-aware report tooltip. Configuration
migrations apply repeatedly without changing existing libraries' off/null defaults.

## Available hardware

An isolated acceptance helper built from the new server/core implementation used the actual
installed media tools on all four hosts. Each host assessed freely licensed 60-second speech,
an Opus candidate, a silent mono candidate and a stereo candidate with only its right channel
silenced. Stereo sources were generated from the licensed mono fixture. A limit of `0.01` was
an explicit fixture policy, not a product recommendation or calibration result.

| Host | Installed tool route | Good mono/stereo | Degraded mono/right channel | Missing worker measurement |
|---|---|---|---|---|
| Local Mac | Packaged macOS app | Passed | Blocked | Blocked |
| PICARD | MSI-installed Windows tools | Passed | Blocked | Blocked |
| Quark | Running Linux sidecar container | Passed | Blocked | Blocked |
| Riker | Running server container | Passed | Blocked | Blocked |

Each case exercised local measurement and full worker evidence consumption. The strict worker
consumer had unusable local media tool paths, proving it could accept complete worker evidence
and reject missing evidence without a server measurement fallback. Healthy decode, duration,
metadata and size checks passed for the degraded cases; their failure came from the new gate.
All source hashes stayed unchanged and owned measurement scratch was empty after completion.
Remote acceptance staging was removed. Pairing, services, real libraries and replacement
settings were not changed by these hardware tests.

This qualifies the new gate and the installed tools, rather than claiming that a newly published
server build has already run every live production queue route. These tests do not establish a
universal listening threshold, surround support or a bitrate recommendation.

## Second-order review and fixes

1. **Previews and listening comparisons:** applying a full-file gate to an intentionally short
   preview would falsely reject it. Both preview verification and calibration policy now exclude
   this gate. Normal completed audio jobs still enforce it.
2. **Older client saves:** omitted new fields must not turn off a saved safety requirement.
   Library updates preserve the existing gate and limit when fields are absent. Invalid explicit
   updates are rejected without weakening the saved policy.
3. **Configuration portability:** exports use format version 2 so older servers reject a backup
   containing a gate they cannot enforce. Version 1 backups remain importable. Missing new fields
   in an old backup preserve a matching audio library's existing gate and limit. Explicitly
   disabled values are honoured.
4. **Worker results:** the existing measurement-request flag is set for either reports or gates.
   No worker protocol change is needed. The server recomputes the maximum from validated raw
   channel values rather than trusting a supplied aggregate or verdict. Missing measurement fails
   closed, including when report-only output is switched off.
5. **Precision:** small channel distances remain visible rather than rounding to zero. The exact
   chosen limit survives saves and navigation. Zero is valid and means no measured difference.
6. **UI consistency:** the horizontal control uses numeric shortcuts and Custom, with touch-sized
   targets and arrow-key support. The points represent distinct numbers rather than calibrated
   quality levels. Custom remains editable even when its number matches a point. Gate-enabled
   reports have their own explanatory tooltip, verdict and historical limit.

## Limits and remaining work

Zimtohrli remains experimental. The implemented gate covers one standalone mono or stereo
audio track. Files up to 90 seconds are assessed throughout; longer files use three 30-second
samples, so unassessed sections have no quality guarantee. Video soundtracks and surround are
not covered. No automatic audio bitrate search or quality retry was added. An unavailable tool
or unsupported input blocks a gated job rather than allowing replacement.

Settings and report screenshots use fabricated documentation data. See the
[configuration reference](../setup/configuration.md#audio-quality-reports-and-gates-development)
and [quality development plan](../development/perceptual-audio-image-quality-plan.md).
