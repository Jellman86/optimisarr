# Precession cube application icon

A beveled cube balances on a single bottom corner. Fifteen horizontal slices share the
original shell. At rest they form a seamless cube; while working a staggered revolution
travels upward through the stack. The bottom slice and its tip remain fixed.

## Application behaviour

- Local dispatch, probing, transcoding, verification and leased remote work start the twist.
  Idle, queued-only and fully suspended work let the pieces settle into alignment.
- The geometry remains stationary at rest. An unseen light orbits every 32 seconds,
  moving the face highlights and the cast shadow. A working cycle takes 8.8 seconds.
- Work is read from the existing activity store and jobs hub. A disconnected hub falls back
  to a visible-page read every 15 seconds. Library scans without a live job are not represented.
- State changes preserve angular position and velocity through critically damped springs.
  The original fifteen meshes remain allocated throughout; there is no replacement solid,
  image crossfade or remeshing when work starts or stops. Gaps close after the slices align.
- Dark mode uses the app's cyan/ice accents (`#22d3ee`, `#a5f3fc`); light mode uses its deeper
  petrol accents (`#0e7490`, `#164e63`). Theme changes update material uniforms without resetting
  motion or the light's phase. The mark receives external light, so there is no emitter glow.
- Reduced motion uses a still cube at rest and a still twisted pose during work. Both have
  dedicated light/dark variants. These sessions never load the graphics module.
- Hidden documents and offscreen marks cancel playback timers and freeze the simulation.
  The favicon is a static PNG, updated only when theme or activity changes.
- Unavailable graphics or a lost context leaves a complete still without retry loops.
  An unavailable 2D canvas uses a theme/activity PNG instead.

## Rendering and resource limits

The renderer uses rasterized triangles with a directional light, diffuse/specular shading,
a softened 1024 × 1024 shadow map, and a transparent ground receiver. The original cube shell
is beveled before slicing, ensuring matching boundaries and normals when all pieces meet.
Geometry uploads once using static buffers. Programs and uniform locations are reused.
Resources are released on unmount or when reduced motion is enabled.

Rendering is capped at 30 fps while turning/settling and 24 fps for idle illumination.
The backing canvas is twice the displayed size, capped at 288 px; a 40 px rail mark renders
at 80 px. A private WebGL canvas is copied immediately to the visible transparent 2D canvas.
There is no retained drawing buffer, ray marching, animation atlas or external graphics library.
The graphics module is lazy-loaded only for a visible mark with motion enabled. Each visible
mark owns a context, sixteen mesh buffers and one shadow target (about 6 MiB for colour/depth,
excluding driver overhead and the small output surfaces).

The production graphics chunk is approximately 11.5 kB (4.7 kB gzip). Each fallback WebP is
under 10 kB and each theme/activity favicon is under 5 kB. Asset tests enforce a 30 kB still
and 15 kB favicon ceiling. These replace roughly 1.5 MB of animation atlases.

## Verification

Unit tests cover the fixed bottom slice, stationary idle geometry, a complete light orbit,
200 interrupted state changes without position/velocity jumps, final seamless alignment,
and closed slice meshes whose combined volume equals the original beveled shell.
Browser tests cover live activity, theme changes, transparency, smooth edges, reduced motion,
unavailable/lost graphics contexts, no graphics downloads with reduced motion, offscreen
scheduling, and mesh reuse across state/theme changes.

A local headless Chromium check of 120 working frames at 288 px, including a synchronous
pixel readback, measured 5.0 ms median and 5.7 ms p95 per render/copy; the first-use maximum
was 143.2 ms. These are local measurements, not a cross-device performance guarantee.

## Rebuilding and reviewing

From `web/`, run:

```sh
node scripts/brand/generate.mjs
npm run check
npm run test:e2e
npm run build
```

The generator starts an isolated Vite server and needs the project's installed Playwright
Chromium (`npx playwright install chromium` on a new checkout). It snapshots the actual
application renderer at 576 px, downsamples to the fallback and favicon sizes, and writes
the packaged favicon/apple-touch PNGs. Geometry, motion and shaders live in `src/lib/brand-*.ts`.

Run `npm run dev`, then open `/scripts/brand/preview.html` to compare both states and themes
at application, rail, tab and enlarged sizes using the actual application player. This is a
local development preview, not an application settings page. For an isolated end-to-end test
server alongside another checkout, set `PLAYWRIGHT_PORT` to an unused local port.
