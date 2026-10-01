# Documentation screenshots

The web screenshots in this directory are captured from the current local Optimisarr UI. All media,
artwork, filenames, connections, workers, measurements, and job histories are fabricated for
documentation. No copyrighted media or production data is used. The small landscape video is
created in Chromium from an original canvas drawing; no external sample media is downloaded.

## Reproduce the web captures

From the repository root:

```bash
npm --prefix web ci
cd web
npx playwright install chromium
node scripts/capture-docs.mjs
```

The capture script starts its own Vite server on `127.0.0.1:4197` and shuts it down when finished.
Set `DOCS_PORT` if that port is occupied. `node scripts/capture-docs.mjs --check-server`
checks only startup ownership and writes no images. It refuses to reuse a server on that port, blocks external
requests, mocks every API route, and fails on unexpected API requests or application errors. It
never connects to an installed Optimisarr server and cannot alter a real library or pairing.
The frontend CI job runs the full capture harness to detect API/UI drift; displayed application
and sidecar versions are read from project metadata.

The script uses English, dark appearance, a fixed documentation clock, and reduced motion. The
standard viewport is 1440 × 1000; the Quarantine review uses 1440 × 1500 to show both the comparison
and decision controls, the quality lab uses 1440 × 1250, and the mobile Queue uses 390 × 1000.
Focused dialogs/cards are captured without unrelated surrounding UI. Some stable filenames remain
aliases for the same current capture so existing documentation links continue to work.

## Coverage

| Area | Captures |
|---|---|
| Dashboard | Full application, main area, savings overview |
| Libraries | Library cards, workflow overview, Choose files, Advanced eligibility and placement, Encode, Advanced encoding, Advanced verification, Schedule & replace, candidates, exclusions |
| Inventory | Full application, main table, artwork-led media dialog |
| Queue | README hero, main view, working-job card, job dialog, mobile view |
| Quarantine | History list, full review with synthetic media players, focused verification report |
| Settings | Overview and all seven rooms, tools, hardware/encoders, backup card |
| Personal quality check | Source selection, video comparison, image comparison |

[`web-screenshot-manifest.json`](web-screenshot-manifest.json) lists every generated web image.
The capture fixtures live in [`docs-fixtures.mjs`](../../web/scripts/docs-fixtures.mjs); the driver
is [`capture-docs.mjs`](../../web/scripts/capture-docs.mjs). They follow the application's API shapes
and exercise its normal routes and controls, without replacing the rendered UI.

After capture, inspect a contact sheet and open detailed images to check text, geometry, loading
states, and fabricated content. Update screenshot captions with the UI labels actually shown, then
run `python3 scripts/check_docs.py` and `git diff --check` from the repository root.

Native sidecar captures use their own platform renderers and are separate from this web harness.

## Dated application review evidence

`review-2026-09-30-*.png` preserves the original review baseline. It may show
issues that have since been fixed. `review-2026-10-01-*.png` shows the tested
mitigation UI, captured by the Playwright layout audit with fabricated media.
The dated Windows compact-monitor image was rendered by the branch's native WPF
renderer on the physical Windows test host; its readings are fabricated too.
These are dated review evidence, not substitutes for the current user-guide
screenshots. See the [review](../reviews/2026-09-30-full-application-review.md)
and [mitigations](../reviews/2026-10-01-review-mitigations.md).
