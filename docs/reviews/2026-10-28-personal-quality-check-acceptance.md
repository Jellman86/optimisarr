# Personal quality check: remaining UI acceptance

This completes the development acceptance follow-up for #441 after the main polish
and video close comparison shipped in 0.2.24. It builds on the feedback from
[@HansVader](https://github.com/Jellman86/optimisarr/issues/441#issuecomment-6362604906)
and the work in #443 and #445. This follow-up is not a new tagged release.

## Findings and fixes

- Preparation, comparison, reveal and starting another check removed the active
  control without moving keyboard focus. Focus now follows the new heading, or
  the new rating group after **Next unrated sample**. A preparation poll preserves
  focus if the user has moved to navigation or another surviving control.
- Errors now have alert semantics, and successful Apply has a polite status
  announcement. Playback failure and a failed preparation-status request offer
  **Check another file**. Stale Apply refusal remains an error, never success.
- Dark primary-button hover text had 3.7:1 contrast. The quality-check page now
  uses a darker hover fill that passes the normal-text contrast check in both themes.
- Enlarged text could squeeze the Original label in the fixed six-column sample
  deck. The deck now sizes its columns to the available width and text size.
- The preset-specific Apply explanation now has a space between its sentences.

The changes use existing translated strings. Encoding, recommendation, playback
timing, gain, worker placement, freshness refusal, verification and source-file
handling are unchanged. All API mutations in this audit go to fabricated routes.

## Reproducible checks

`web/e2e/calibration.spec.ts` contains the acceptance tests and optional capture
mode. `web/e2e/helpers/quality-check-media.ts` generates the landscape, playable
VP8 video and low-level PCM tone. Every variant uses the same generated content;
ratings, savings, codec names and VMAF values are fabricated interface examples.
They are not measured quality evidence. No production server, private library,
real media file or pairing is used. API and SignalR traffic are intercepted.

The fixture now matches the server's diagnostics opt-in: the ordinary comparison
does not receive diagnostic metadata. Video captures explicitly select **Compare
presets**, because their fabricated results compare different profiles. Music and
photos use their corresponding source guidance, controls and result units.

The matrix covers video, music and photos in both light and dark themes:

- Setup, comparison and results at 1440 × 1400 CSS pixels.
- Comparison at 390 × 844, with vertically scrolling controls.
- Results with root text at 200% and the original viewport width.
- Results at 720 × 700 CSS pixels, the content-viewport equivalent of a
  1440 × 1400 browser at 200% zoom. This does not automate browser chrome zoom.

All 36 matrix states run axe WCAG 2 A/AA and 2.1 AA checks on `.quality-lab`, with
no rule exclusions. The tests also check document/main horizontal overflow,
control containment and browser runtime errors. Primary-button hover is included.
Keyboard tests exercise all ratings through Reveal and Apply using Tab/Enter,
plus preparation failure, focus preservation and returning to source selection.
State tests cover empty sources, waiting for a worker, preparation/playback/status
errors, no acceptable candidate and stale Apply refusal. Existing frame-switch,
fullscreen, zoom/pan, pending-rating and diagnostics tests remain in the suite.

This is automated Chromium coverage and visual inspection of the captures. It
does not certify a particular screen reader/browser combination, translated copy,
human listening preference, browser colour/HDR fidelity or native worker media
acceptance. Those are separate from this UI follow-up.

To regenerate captures while running the acceptance matrix:

```sh
QUALITY_CAPTURE_DIR=/path/on/developer-ssd/captures npm --prefix web run test:e2e -- \
  calibration.spec.ts --grep 'accessibility and capture acceptance' --workers=2
```

Screenshots expand only viewport height after the layout checks, so the nested
scrolling content is captured in full. Width and text scale remain unchanged.
The manifest records tested viewports, source hashes, image dimensions and hashes.
The versioned subset below has 27 PNGs; all 36 captures and axe reports remain in
the local `optimisarr-ui441-20261028` artifact directory.

Validation on this change: 384 browser tests, 104 frontend unit tests and 3,612
backend tests pass. Svelte check has zero errors/warnings, all eight locale audits
pass, the .NET build has zero warnings, and the frontend production build succeeds.
The 161 Python tests and Unraid metadata validation also pass. The matrix records
zero detected axe violations and zero browser runtime errors in all 36 states.

## Captures

| Media | Dark setup | Dark comparison | Dark results | Light setup | Light comparison | Light results |
| --- | --- | --- | --- | --- | --- | --- |
| Video | [Setup](../images/reviews/personal-quality-check-20261028/video-dark-setup.png) | [Compare](../images/reviews/personal-quality-check-20261028/video-dark-compare.png) | [Results](../images/reviews/personal-quality-check-20261028/video-dark-results.png) | [Setup](../images/reviews/personal-quality-check-20261028/video-light-setup.png) | [Compare](../images/reviews/personal-quality-check-20261028/video-light-compare.png) | [Results](../images/reviews/personal-quality-check-20261028/video-light-results.png) |
| Photos | [Setup](../images/reviews/personal-quality-check-20261028/image-dark-setup.png) | [Compare](../images/reviews/personal-quality-check-20261028/image-dark-compare.png) | [Results](../images/reviews/personal-quality-check-20261028/image-dark-results.png) | [Setup](../images/reviews/personal-quality-check-20261028/image-light-setup.png) | [Compare](../images/reviews/personal-quality-check-20261028/image-light-compare.png) | [Results](../images/reviews/personal-quality-check-20261028/image-light-results.png) |
| Music | [Setup](../images/reviews/personal-quality-check-20261028/audio-dark-setup.png) | [Compare](../images/reviews/personal-quality-check-20261028/audio-dark-compare.png) | [Results](../images/reviews/personal-quality-check-20261028/audio-dark-results.png) | [Setup](../images/reviews/personal-quality-check-20261028/audio-light-setup.png) | [Compare](../images/reviews/personal-quality-check-20261028/audio-light-compare.png) | [Results](../images/reviews/personal-quality-check-20261028/audio-light-results.png) |

| Media | Narrow comparison | 200% root text | 200% zoom-equivalent viewport |
| --- | --- | --- | --- |
| Video | [390px](../images/reviews/personal-quality-check-20261028/video-dark-compare-narrow.png) | [Text](../images/reviews/personal-quality-check-20261028/video-light-results-text-200.png) | [Viewport](../images/reviews/personal-quality-check-20261028/video-light-results-zoom-200.png) |
| Photos | [390px](../images/reviews/personal-quality-check-20261028/image-dark-compare-narrow.png) | [Text](../images/reviews/personal-quality-check-20261028/image-light-results-text-200.png) | [Viewport](../images/reviews/personal-quality-check-20261028/image-light-results-zoom-200.png) |
| Music | [390px](../images/reviews/personal-quality-check-20261028/audio-dark-compare-narrow.png) | [Text](../images/reviews/personal-quality-check-20261028/audio-light-results-text-200.png) | [Viewport](../images/reviews/personal-quality-check-20261028/audio-light-results-zoom-200.png) |

[Capture manifest](../images/reviews/personal-quality-check-20261028/manifest.json).
