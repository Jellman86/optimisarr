# UI layout audit

The Playwright layout audit is a regression gate for the operational UI. Its
first target was a repeated defect where a control group stopped well before
the edge of its card. It also checks overflow and clipping across the app, so
new nested settings pages cannot silently reintroduce the same class of bug.

## Coverage

[`web/e2e/layout-audit.spec.ts`](../../web/e2e/layout-audit.spec.ts) visits all
30 current app routes: Dashboard; Libraries and its source, encode, video,
audio, verification, automation and advanced rooms; photo image settings;
Inventory; Queue; Quarantine and its review; Schedule; every Settings room; and
Personal quality check. It uses fabricated media, jobs, workers and connection
data from `web/scripts/docs-fixtures.mjs`. No personal library or running server
is required.

Each route is measured at 1440 px dark, 1600 px light, 1024 px light, 812 px
landscape, 390 px dark, 320 px light, and 1440 px light with 200% root text.
Representative phone routes also run in all nine shipped languages at 320 px.
Separate checks open Inventory and Queue details at desktop and phone sizes,
and exercise interactive-card hover and keyboard focus. The existing E2E suite
covers setup, loading and error states, settings tooltips, job interactions,
quarantine decisions and other workflows; the layout audit runs alongside it.

The geometric assertions reject document or main-area horizontal overflow,
cards extending outside the main area, card contents clipped horizontally, and
wide Settings switch rows that fail to use their card. Inputs and paragraphs
may have a readable maximum width; a toggle row must reach the usable card
edge. Dialogs must fit the viewport and keep their content scrollable. The test
also rejects unmocked API calls and uncaught page errors.

Desktop dark and phone dark screenshots are saved for every route. The enlarged
text run saves representative screenshots; any layout failure attaches its
screenshot and measurements. These are review evidence, not brittle exact-pixel
snapshots: human review remains necessary for spacing, hierarchy, artwork,
contrast and awkward but technically un-clipped wrapping.

## Run it

From `web/`:

```bash
npm ci
npx playwright install chromium
npm run audit:ui
npm run test:e2e
```

On a Mac, install the matching WebKit browser and run the optional Safari-engine
pass:

```bash
npx playwright install webkit
npm run audit:ui:webkit
```

Playwright stores screenshots, measurements, traces and failure context under
`web/test-results/`. The normal `npm run test:e2e` command includes the
Chromium layout audit in CI. Review representative images at each breakpoint,
especially below-the-fold controls after a layout change; then run the full
functional suite. Update the route inventory whenever a page is added.

## Findings from the first pass

- Settings → Media servers and Notifications had switch groups capped at a
  narrow width inside full-width cards. Their switches now align with the card.
- Advanced library quality and image-quality controls clipped at 320 px. Labels
  now wrap, and slider values move below the range when necessary.
- Personal quality check's fixed side column squeezed the main panel at 200%
  text. Its columns now depend on the available content width and stack when
  needed.
- Linux Chromium exposed additional 200% text pressure in Dashboard metrics
  and worker details. Those grids now follow their card widths. The sidebar
  language list stays hidden until its measured position is applied, avoiding
  a brief offscreen jump when the rail changes size.

The audit is browser-rendered UI verification with deterministic fixtures. It
does not certify live encoder data, native sidecar panels, or a real user's
theme/font overrides. Those need their own integration and device checks.
