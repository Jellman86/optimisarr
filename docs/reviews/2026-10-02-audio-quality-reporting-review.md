# Audio quality reporting implementation review

Scope: experimental, opt-in audio observations in development PR #343. Automatic perceptual
replacement gates, surround comparisons, audio quality searches and duplicate detection remain
future work. Production hosts were not upgraded during these tests.

## Behaviour and safety

- New and existing libraries start with reporting off. The schema migration preserves existing
  rows, repeated migration preserves an explicit opt-in, and configuration export/import retains
  the choice. An older update request that omits the field preserves the saved choice.
- The library Verify and Advanced verification views share the draft. Queue and Quarantine use
  one report component with theme tokens, responsive channel cards and an accessible tooltip.
- Only complete standalone audio jobs request observations. Previews, video jobs, unsupported
  layouts and damaged candidates cannot acquire misleading scores.
- Reports remain separate from replacement checks. Missing tools or failed optional measurements
  produce unavailable reports. Cancellation propagates. Strict worker jobs never launch a server
  assessment when worker observations are missing or invalid.
- The server validates the pinned metric/preparation, file hashes, profiles, complete windows,
  channel counts and finite distances before displaying worker scores. Existing file guards and
  content identity checks still surround verification and replacement.

## Second review findings and fixes

1. Queue's descendant `dl` styles removed padding inside the measurement cards. Restrict those
   selectors to the direct job specification list. Browser tests assert card padding and no
   horizontal overflow at 375 and 1440 pixels in both themes.
2. A cleanup or launch exception from an optional provider could fail a job despite valid gate
   evidence. Turn these ordinary measurement errors into unavailable observations. Test the
   exception path while keeping cancellation distinct.
3. CMake on a newer Mac could build a metric requiring that OS, despite the app supporting
   macOS 14. Pin the deployment target to 14 and test the Mach-O minimum OS metadata.
4. Packaging a new executable also requires its signature, licences and source provenance.
   Sign it inside the Mac bundle, include it in the Windows MSI and Docker runtimes, and add
   pinned Zimtohrli/Highway sources to the sidecar corresponding-source records.

## Validation

| Area | Evidence |
| --- | --- |
| Backend | Build with zero warnings; 2,619 tests passed, including migration, backup, policy, observation failure and strict remote isolation |
| Browser | 206 tests passed; focused report cases pass in light/dark themes at phone and desktop widths; locale/type checks clean |
| Shared worker | 285 tests passed |
| Mac sidecar | 259 tests passed, release build passed, native metric tests passed |
| Mac packaging | Relocated ad-hoc signed app contains the metric/tools/icons; all 20 rendered native fixtures passed |
| Actual devices | Licensed music passed the shared full-verification/report path on this Mac, PICARD, Quark sidecar and Riker container; Swift path passed on this Mac |
| Resource isolation | The injected server fallback was never called; owned scratch emptied; both live containers stayed healthy with zero restarts |
| Documentation | OpenAPI current; links, metadata and 74 script tests passed; focused report screenshot generated from fabricated data |

CI still needs to qualify the new Windows MSI and both final Docker targets before merge.
Hardware evidence used isolated executables and the installed media tools. It does not claim
an end-to-end production fleet rollout, a Windows native UI change or a calibrated pass threshold.
