# Captive light application icon

The mark is a point emitter inside the projected 16 vertices and 32 edges of a tesseract.
Its spectral light shafts turn slowly at rest and accelerate during work; the cage gently
changes its projection.
The bars catch the directional emission and occlude the surrounding volume. This is a
single-scattering light-field approximation with an angular shadow map, not a full path tracer
or a simulation of glass refraction. The point source stays at the centre.

## Application behaviour

- Local dispatch, probing, transcoding, verification and leased remote work excite the light.
- Idle, queued-only and fully suspended work use a quiet 36-second cycle at 12 fps. Active
  work accelerates the same geometry to a six-second cycle at 24 fps. Idle light is dimmed
  to 78% opacity; adjacent poses blend in both states.
- Work is read from the existing activity store and jobs hub. A disconnected hub falls back
  to a visible-page read every 15 seconds. Library scans without a live job are not represented.
- A 450 ms fade lets the light settle. Reduced motion switches between steady and brighter
  stills without animation. Theme changes preserve geometry and provide light-theme contrast.
- Hidden documents and offscreen marks cancel their playback timers. The favicon changes
  only when work changes state.
- An unavailable animation leaves a complete still; an unavailable canvas uses the packaged PNG.

## Rendering and resource limits

Light transport is calculated **offline**, using capsule ray intersections for all 32 bars,
a 512 × 256 angular visibility map, and 72 importance-sampled steps through the light volume.
The illumination function also drives the frame's diffuse/specular response, so rotating a
shaft changes the highlight at its intersection with a bar. Volume integration stops at the
nearest visible bar and the visibility map shadows points beyond the obstructing geometry.

Each pose is rendered at 576 × 576, then downsampled to a 288 px atlas tile. The main mark is
144 CSS px with a 288 px backing canvas, giving another downsample at display time. Small marks
use tighter framing and an 80 px tile. Smooth image filtering replaces all pixelated rendering.
This supersampling matters: WebGL MSAA alone cannot antialias an implicit capsule silhouette
computed inside a full-screen fragment shader.

There are 48 poses in a seamless loop, shared by both playback speeds. Playback interpolates two adjacent poses
at at most 24 fps, using additive premultiplied contributions to preserve the shaft brightness.
No WebGL context, shader compilation or ray marching runs in the application. All marks share
the decoded images; the large atlas is requested only for a visible, animated large mark.

| Asset | Transfer bytes | Decoded atlas pixels |
| --- | ---: | ---: |
| Expanded animation | 1,257,178 | 2304 × 1728 (about 15.2 MiB RGBA) |
| Small animation | 259,998 | 640 × 480 (about 1.2 MiB RGBA) |
| Steady still | 19,234 | 288 × 288 |
| Working still | 26,632 | 288 × 288 |

`brand-assets.test.ts` enforces 1.5 MB / 300 kB animation ceilings and 30 kB / 40 kB still
ceilings. Reduced-motion sessions do not fetch either animation atlas. A visible idle mark
loads the same atlas as a working mark, with no additional animation assets. Downloaded
atlases remain cached for reuse during the page session; this trades bounded decoded memory
for avoiding repeated downloads and decoding on navigation.

## Measurements and verification

Initial atlas implementation measured on local headless Chromium/ANGLE SwiftShader on
16 September 2026 (before the subsequent slow-idle playback change):

- The live prototype at a 288 px backing resolution took approximately 42–220 ms per render
  invocation, including its two thumbnail copies and synchronous GPU completion request. This
  software-rendering path was the reason to bake the light transport rather than run it live.
- In the production build, the new main mark ran at 23.95 paints/second, with zero WebGL contexts.
  Mean `drawImage` submission time was 0.013 ms per call (observed maximum 0.10 ms). These are
  CPU submission timings, not end-to-end GPU frame times or a claimed cross-device speedup.
- Current instrumented browser tests confirm no further paints while offscreen, slow motion
  while idle or suspended, faster geometry after a jobs event, and a held brighter frame for
  reduced motion. Missing-atlas and reduced-motion no-atlas-download paths are covered.
- Inspected the production build in both themes, at 144 px and 40 px, and at a 375 px mobile
  viewport. Inspected the enlarged scene for bar highlights, shaft occlusion and smooth edges.

Full validation: zero-warning .NET build; 2,019 backend tests; clean Svelte/TypeScript and
locale checks; 20 frontend unit tests; 93 browser tests; successful production frontend build.
The build retains the existing advisory about the main application bundle exceeding 500 kB.

## Rebuilding and reviewing

From `web/`, run:

```sh
node scripts/brand/generate.mjs
npm run check
npm run test:e2e
```

The generator needs the project's installed Playwright Chromium (`npx playwright install
chromium` on a new checkout). It writes the two atlases, steady/working WebP and favicon PNG
assets, and the packaged favicon/apple-touch PNGs. `scripts/brand/scene.html` is the editable
shader and geometry source; none of the generator or scene is included in the application bundle.

Run `npm run dev`, then open `/scripts/brand/preview.html` to compare both states and themes
at application, rail, tab and enlarged sizes using the actual application player. This is a
local development preview, not a new application settings page.
