# Personal quality check: UI polish evidence

Captured on 23 October 2026 from the `feat/personal-quality-check-polish` branch on dev
`7740a88d`, using Chromium through Playwright. It follows the
[22 October baseline](2026-10-22-personal-quality-check-ui.md) and issue #441.

As before, every API response, file name, library name, classification, saving and quality
score is a controlled fixture. The landscape and playable video were generated in a browser
canvas, and the music player uses a generated low-volume tone. The samples are identical within
each media kind. These captures review interface states; they do not measure or certify quality.
No production server or private library data was used. Diagnostics stayed off.

The harness is the baseline harness with updated rating labels and two fixture corrections.
Quality-only video results no longer report a recommended preset, and results carry the saved
library setting they would replace. Desktop captures use 1440 × 1400 and narrow captures use
390 × 844. The harness also captured a 720 × 700 view, equivalent to 200% zoom, for each media
kind, plus the video flow in the light theme. All 23 captures recorded zero browser runtime errors
and no horizontal overflow. The adjacent manifest lists the versioned subset. The rest remains in
the SSD artifact store `personal-quality-check-ui-20261023`.

## What changed

- A four-step indicator shows source, preparation, blind comparison, and reveal/apply. On narrow
  screens it collapses to one row.
- Music uses listening language: "How does this sample sound?", "Audibly worse", "I cannot
  reliably hear a difference", and excerpts instead of scenes. A compact listening header replaces
  the empty video-shaped stage. The setup safety copy names clips, excerpts or test images to match
  the media kind.
- Rating is locked while a sample switch is pending, so a rating always applies to the sample on
  screen. The sample deck marks unrated candidates. A "Next unrated sample" action moves through
  the remaining candidates.
- Photo inspection has explicit Fit and 1:1 (actual pixels) controls and a zoom readout. A pan
  hint appears when zoomed. Zoom and position stay matched across samples.
- Results first give the recommendation and explain that it came from the ratings. They then show
  the exact Now → After library setting and label the saving as an estimate. They also explain
  that Apply changes only that setting for future work, queues nothing and replaces no file. When
  presets were compared, they add that the preset's defaults are adopted. VMAF is labelled as
  reference evidence that did not choose the result. The existing media-specific personal-result
  caveat is shown. A "Check another file" action is offered after results, after a no-preference
  result and after a preparation failure.

## Video

- [Setup](../images/reviews/personal-quality-check-20261023/video-setup.jpg)
- [Blind comparison](../images/reviews/personal-quality-check-20261023/video-compare.jpg)
- [Narrow comparison](../images/reviews/personal-quality-check-20261023/video-compare-mobile.jpg)
- [Revealed result](../images/reviews/personal-quality-check-20261023/video-results.jpg)
- [Revealed result, light theme](../images/reviews/personal-quality-check-20261023/video-results-light.jpg)

## Photos

- [Setup](../images/reviews/personal-quality-check-20261023/image-setup.jpg)
- [Blind comparison](../images/reviews/personal-quality-check-20261023/image-compare.jpg)
- [Narrow comparison](../images/reviews/personal-quality-check-20261023/image-compare-mobile.jpg)
- [Revealed result](../images/reviews/personal-quality-check-20261023/image-results.jpg)

## Music

- [Setup](../images/reviews/personal-quality-check-20261023/audio-setup.jpg)
- [Blind comparison](../images/reviews/personal-quality-check-20261023/audio-compare.jpg)
- [Narrow comparison](../images/reviews/personal-quality-check-20261023/audio-compare-mobile.jpg)
- [Narrow rating panel](../images/reviews/personal-quality-check-20261023/audio-compare-mobile-rating.jpg)
- [Revealed result](../images/reviews/personal-quality-check-20261023/audio-results.jpg)

## Unchanged

Blind labels, the diagnostics warning, preparation/worker placement, freshness (stale-setting)
refusal on Apply, verification and original-file safety are unchanged. Recommendation logic and
encoding settings are also unchanged. The only API change is that the revealed result now
includes the saved quality, plus the preset for video, that was captured when the check started.
