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
and decision controls, the quality lab uses 1440 × 1250, Exact copies uses 1440 × 1400,
the soundtrack report uses 1440 × 1900, and the mobile Queue uses 390 × 1000.
Focused dialogs/cards are captured without unrelated surrounding UI. Some stable filenames remain
aliases for the same current capture so existing documentation links continue to work.

## Coverage

| Area | Captures |
|---|---|
| Dashboard | Full application, main area, savings overview |
| Libraries | Library cards, workflow overview, Choose files, Advanced eligibility and placement, Encode, Advanced encoding, Advanced verification, Schedule & replace, candidates, exclusions |
| Inventory | Full application, main table, artwork-led media dialog |
| Queue | README hero, main view, working-job card, job dialog, mobile view, playback holds with and without viewer names |
| Quarantine | History list, full review with synthetic media players, focused verification report |
| Settings | Overview and all seven rooms, tools, hardware/encoders, backup card, viewer-privacy edit and saved state |
| Schedule | Schedule controls and playback holds |
| Personal quality check | Source selection, video comparison, image comparison |

[`web-screenshot-manifest.json`](web-screenshot-manifest.json) lists every generated web image.
The capture fixtures live in [`docs-fixtures.mjs`](../../web/scripts/docs-fixtures.mjs); the driver
is [`capture-docs.mjs`](../../web/scripts/capture-docs.mjs). They follow the application's API shapes
and exercise its normal routes and controls, without replacing the rendered UI.

After capture, inspect a contact sheet and open detailed images to check text, geometry, loading
states, and fabricated content. Update screenshot captions with the UI labels actually shown, then
run `python3 scripts/check_docs.py` and `git diff --check` from the repository root.

## Sidecar captures

The [user gallery](../usage/screenshots.md#sidecars) links the current native and Linux images.
All job state, telemetry, machine names and addresses are fabricated. Spectrograms on Mac,
Windows and Linux are measured from the committed original fixture
[`sidecar-spectrum.jpg`](../../web/scripts/fixtures/sidecar-spectrum.jpg), generated from this
three-second chirp, not private audio:

```bash
ffmpeg -hide_banner -loglevel error -f lavfi \
  -i 'aevalsrc=0.2*sin(2*PI*(220*t+300*t*t)):s=48000:d=3' \
  -filter_complex '[0:a:0]aresample=48000,showspectrumpic=s=320x96:legend=0:scale=log:fscale=log:color=viridis:mode=combined[spectrum]' \
  -map '[spectrum]' -frames:v 1 -q:v 6 -y /tmp/generated-spectrum.jpg
```

### Linux

```bash
cd web
node scripts/capture-linux-sidecar.mjs
# Optional: pass an alternative generated spectrum JPEG as the first argument.
```

This captures audio, video encoding, idle and pairing in dark, light and phone layouts. It
starts its own Vite server on `127.0.0.1:4219`, refuses an occupied port, blocks external and
unexpected API requests, and never submits a pairing code. It uses the shipped
[`Sidecar.svelte`](../../web/src/Sidecar.svelte), a fixed clock, English, UTC and reduced motion.
The default is the committed generated chirp spectrum; a supplied spectrum must fit the
worker's 8 KiB preview limit. Layout overflow, application errors or an unloaded preview fail the capture. The original `capture-audio-sidecar.mjs`
command remains available for audio-only refreshes.

[`linux-sidecar-screenshot-manifest.json`](linux-sidecar-screenshot-manifest.json) records the
capture date, source revision, version, viewport variants, spectrum hash and image hashes.

### Native Mac and Windows

Use the apps' isolated fixture renderers. These instantiate their actual native views with
fabricated state; they never pair with a server or claim work:

```bash
# macOS: release app or locally built executable
swift build --package-path sidecars/macos -c release
OPTIMISARR_RENDER_SPECTRUM="$PWD/web/scripts/fixtures/sidecar-spectrum.jpg" \
OPTIMISARR_RENDER_FRAME="$PWD/web/scripts/fixtures/sidecar-frame.png" \
  sidecars/macos/.build/release/OptimisarrSidecar --render-menu /tmp/mac-sidecar-captures
```

```powershell
# Windows: run on Windows after building the app
$env:OPTIMISARR_RENDER_SPECTRUM = 'C:\path\to\optimisarr\web\scripts\fixtures\sidecar-spectrum.jpg'
.\Optimisarr.Sidecar.Tray.exe --render-monitor C:\Temp\windows-sidecar-captures
```

The renderer output names are recorded alongside their stable documentation names in
[`native-sidecar-screenshot-manifest.json`](native-sidecar-screenshot-manifest.json), with
SHA-256 hashes and source provenance. Most names gain `optimisarr-sidecar-macos-` or
`optimisarr-sidecar-windows-`; `shutdown-countdown` becomes `shutdown`, Mac `connected-idle`
becomes `idle`, and Mac `unpaired` becomes `pairing`. Light audio retains the older
`audio-light` suffix. Copy only the listed states, retaining existing documentation filenames.

The 2026-10-09 refresh uses local release builds of the reviewed Mac and Windows UI with
0.2.23 version metadata, corrected capture fixtures and native WPF renders on Windows.
Mac `OPTIMISARR_RENDER_FRAME` supplies an original generated landscape instead of the
renderer's default colour ramp. Without a bundle the Mac executable reports an unknown
build; its source revision and renderer hashes are recorded in the manifest. These capture
commands do not modify the installed apps.

Windows CI also supplies the committed spectrum fixture. Its `windows-sidecar-ui` artifact can
be used for a separate capture with that exact run recorded in the manifest:

```bash
gh run download <run-id> --repo jellman86/optimisarr \
  --name windows-sidecar-ui --dir /tmp/windows-sidecar-captures
```

Mac snapshots use a flat background and bordered controls because offscreen rendering cannot
capture the live window-server glass; on macOS 26 the live popover uses system glass.
Indeterminate activity bars freeze in native snapshots and do not show a completion percentage;
encoded time remains readable. The focused Mac details crop ends at the final technical row.
These rendering limits are not measurements of a live fleet.

## Dated application review evidence

`review-2026-09-30-*.png` preserves the original review baseline. It may show
issues that have since been fixed. `review-2026-10-01-*.png` shows the tested
mitigation UI, captured by the Playwright layout audit with fabricated media.
The dated Windows compact-monitor image was rendered by the branch's native WPF
renderer on the physical Windows test host; its readings are fabricated too.
These are dated review evidence, not substitutes for the current user-guide
screenshots. See the [review](../reviews/2026-09-30-full-application-review.md)
and [mitigations](../reviews/2026-10-01-review-mitigations.md).
