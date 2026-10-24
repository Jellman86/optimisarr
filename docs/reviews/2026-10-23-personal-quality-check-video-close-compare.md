# Personal quality check: close-up video comparison evidence

Captured on 23 October 2026 from the `feat/quality-check-video-close-compare` branch on dev
`f9957842`, using Chromium through Playwright. It follows the
[UI polish evidence](2026-10-23-personal-quality-check-ui-polish.md) and the video feedback on
issue #441: without a close-up view and fast switching in full screen, comparing became guessing.

API responses are controlled fixtures. The five 1280 × 720 VP9 clips were rendered for this
review, and the candidates are degraded on purpose: A is softened, B is banded, C is heavily
softened and D is blocky. The clips show interface behaviour. They do not measure quality or
certify HDR or colour fidelity. No production server or private library data was used, and
diagnostics stayed off.

Each synthetic frame carries a small barcode of its scene frame number. The original has 18
lead-in frames (0.75 s), like a reference whose sample starts later in its file. The harness reads
the barcode from the decoded `<video>` picture to see which frame the browser really shows. The
harness, clips and remaining captures are in the SSD artifact store
`personal-quality-check-video-compare-20261023`; only the screenshots below are versioned.

## What changed

- Video gains the photo inspection controls: Fit, zoom out/in, 1:1 (actual pixels of the
  original) and drag-to-pan. Pause for a still frame. Zoom, position and playback time stay
  matched when you switch samples. The controls are hidden in temporary stream verification mode,
  where native browser controls own the picture.
- Full screen shows an O/A/B/C/D switcher under the picture, because the sample deck is out of
  view there. Pressing O or a sample's letter switches samples anywhere on the comparison page,
  except while typing into a field. The deck shows the shortcut too.
- A switch still waits until the chosen sample has loaded and seeked before it is shown or named.
  A seek must now also land where it was sent. Browsers clamp an unseekable seek, for example
  without HTTP range support, instead of failing it. That is now reported as a playback error
  instead of being presented as the matched frame.
- Pausing re-seeks the video to its own position. Chromium can leave the frame after
  `currentTime` on screen at pause. Without the re-seek, the first switch after a pause could
  compare frames one apart.

## Real-browser checks

All 15 checks passed in each of six runs after the pause fix:

- At 5 s, the original and A, C and B (switched by key) all showed scene frame 120. The original
  plays from 5.75 s in its file and the candidates from 5 s.
- Zoom (200%) and pan were unchanged across key switches.
- Rapid keys `a o d o b` in full screen settled on B at the matched frame without an error.
- Switching while playing resumed playback.
- After pausing at an arbitrary time, A, D and the original showed the same frame. Before the
  pause re-seek, one run showed frame 162 at pause and frame 161 after switching back.
- Responses without range support produced the playback error, not a matched-frame claim.
- The 390 × 844 layout had no horizontal overflow. No page errors were recorded.

## Screens

- [Close-up of the original at 200%](../images/reviews/personal-quality-check-video-compare-20261023/video-zoom-original.jpg)
- [Same close-up after pressing C](../images/reviews/personal-quality-check-video-compare-20261023/video-zoom-c.jpg)
- [Full screen with the sample switcher](../images/reviews/personal-quality-check-video-compare-20261023/video-fullscreen-zoom.jpg)
- [Comparison page with video controls](../images/reviews/personal-quality-check-video-compare-20261023/video-compare.jpg)
- [Narrow viewer](../images/reviews/personal-quality-check-video-compare-20261023/video-compare-mobile.jpg)

## Unchanged

Blind labels, the opt-in diagnostics and its warning, rating locks during a switch, ratings,
recommendation logic, Apply and its freshness checks, encoding, verification and original-file
safety are unchanged. There are no API or schema changes. The design keeps one native video
element, so switching reloads the chosen sample's stream. It is quick for cached short samples,
but the picture is blank until the matched frame is ready rather than showing a stale one.
Side-by-side and divider layouts were left out in favour of one matched, zoomable view.
