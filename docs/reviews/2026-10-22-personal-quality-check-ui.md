# Personal quality check: current UI evidence

Captured on 22 October 2026 from dev source `c4470601d7f6783aa3dad24d33e8fc3c7549a127`
using Chromium through Playwright. These are screenshots of the actual current
Svelte UI, not redesign mockups.

All API responses, file names, library names, classifications, savings and quality
scores are controlled example fixtures. The landscape and playable video were
generated in a browser canvas; the music player uses a generated low-volume tone.
The samples are deliberately identical within each media kind: these captures
review interface states, not encoding differences or measured quality. They do
not certify audio/video/image fidelity, HDR, colour accuracy or perceptual targets.
No production server or private library data was used. Diagnostics remained off.

The harness follows `web/e2e/calibration.spec.ts` and the isolated, fabricated
capture pattern in `web/scripts/capture-docs.mjs`. It opens setup, prepares samples,
selects anonymous A, captures the narrow layout, rates each candidate, and reveals
the result. Video switches wait for the chosen sample to become active before
rating. Example results mark D/E as worse, consistently with the recommended C.
Desktop captures use 1440 × 1400; narrow captures use 390 × 844. Browser runtime
errors: zero. The adjacent manifest records every screenshot and source baseline.
The generation script and generated media remain in the SSD artifact store,
`personal-quality-check-ui-20261022`; only the small review images are versioned.

## Video

- [Choose source and prepare](../images/reviews/personal-quality-check-20261022/video-setup.jpg)
- [Blind comparison](../images/reviews/personal-quality-check-20261022/video-compare.jpg)
- [Revealed result](../images/reviews/personal-quality-check-20261022/video-results.jpg)
- [Narrow comparison layout](../images/reviews/personal-quality-check-20261022/video-compare-mobile.jpg)

## Photos

- [Choose source and prepare](../images/reviews/personal-quality-check-20261022/image-setup.jpg)
- [Blind comparison](../images/reviews/personal-quality-check-20261022/image-compare.jpg)
- [Revealed result](../images/reviews/personal-quality-check-20261022/image-results.jpg)
- [Narrow comparison layout](../images/reviews/personal-quality-check-20261022/image-compare-mobile.jpg)

## Music

- [Choose source and prepare](../images/reviews/personal-quality-check-20261022/audio-setup.jpg)
- [Blind comparison](../images/reviews/personal-quality-check-20261022/audio-compare.jpg)
- [Revealed result](../images/reviews/personal-quality-check-20261022/audio-results.jpg)
- [Narrow comparison layout](../images/reviews/personal-quality-check-20261022/audio-compare-mobile.jpg)

## Review scope

Keep one understandable source → prepare → compare → reveal → apply workflow,
with clear stage and sample progress. Keep the original easy to reach, make rating
and sample switching unambiguous, and explain preparation/waiting/failure states.

Video needs a stable comparison player and clear scene navigation; photos need
clear fit/1:1/zoom and aligned original/candidate inspection; music needs listening
language and efficient A/B playback controls rather than a video-shaped empty
stage. The current music view still asks how a sample “looks” and offers “Visibly
worse”; the shared original-safety copy still describes disposable clips in
photo mode.
These are observed wording/layout points, not allegations of media corruption.

The result should lead with the preference on these samples, explain why the
setting is recommended, distinguish subjective ratings from objective evidence,
and show the exact library setting change before Apply. Preserve the saved codec,
container and other settings unless explicitly selected for comparison. Explain
that estimates are estimates and that a personal sample preference does not
establish a universally calibrated metric target.

Check keyboard/focus, screen readers, both themes, 200% zoom and narrow layouts.
Keep preparation, unsupported/empty inputs, worker waiting, playback failure, no
acceptable candidate and stale-result refusal explicit. Preserve blind candidate
labels until Reveal, warn before diagnostics break blindness, and retain existing
verification, worker placement, freshness and original-file safety rules.

This evidence change does not implement that polish or change application behaviour.
