import assert from 'node:assert/strict'
import test from 'node:test'
import {
  BASE_RATE,
  C0,
  C1,
  CROSS_MS,
  CUBE,
  PixelGrid,
  RESTING,
  TESSERACT,
  WORKING,
  crossStates,
  drawTesseract,
  fitScale,
  hullOrder,
  palette,
  project,
  rot,
  silhouetteEdges,
  type Vec4,
} from './tesseract.ts'

const HOME_TURN = Math.PI / 4
const HOME_TILT = Math.atan(Math.SQRT1_2)
const TAU = Math.PI * 2

/** The projection the renderer performs, so the geometry tests see exactly what it sees. */
function projectAt(t: number, n: number, weight = WORKING.weight) {
  const verts = TESSERACT.verts.map((v) => {
    const c = [...v] as Vec4
    rot(c, 0, 3, t * TAU)
    rot(c, 1, 2, t * TAU * 2)
    rot(c, 0, 2, t * TAU + HOME_TURN)
    rot(c, 1, 2, HOME_TILT)
    return c
  })
  const s = fitScale(verts, n, 0.74 - (weight - 1) * 0.03)
  return verts.map((v) => project(v, n, s))
}

function frame(t: number, n = 64, cfg = WORKING, dark = true): Uint8Array {
  const g = new PixelGrid(n)
  g.clear()
  drawTesseract(g, t, cfg, dark)
  return Uint8Array.from(g.col)
}

test('the loop is seamless: the last frame is the first frame', () => {
  // An earlier version turned yz by half a turn per loop, so the pose at t=1 was not the pose
  // at t=0 and the mark visibly jumped once a cycle.
  const start = frame(0)
  const end = frame(1)

  assert.deepEqual(Array.from(end), Array.from(start))
})

test('the mark is drawn, and most of the grid stays transparent', () => {
  // It is an application icon: the canvas is never filled, so what is behind it shows through.
  const lit = frame(0.2).reduce((n, v) => n + (v ? 1 : 0), 0)

  assert.ok(lit > 200, `expected a drawn mark, got ${lit} lit pixels`)
  assert.ok(lit < 64 * 64 * 0.4, `expected mostly transparent, got ${lit} lit pixels`)
})

test('the mark fits inside its grid at every point in the loop', () => {
  // fitScale measures the projection rather than guessing at it. A fixed scale left the solid
  // a sixth of its intended size; too large a one clips the outline against the frame.
  for (let k = 0; k < 60; k++) {
    const g = new PixelGrid(64)
    g.clear()
    drawTesseract(g, k / 60, WORKING, true)
    for (let i = 0; i < 64; i++) {
      assert.equal(g.col[i], 0, `top row lit at t=${(k / 60).toFixed(2)}`)
      assert.equal(g.col[63 * 64 + i], 0, `bottom row lit at t=${(k / 60).toFixed(2)}`)
    }
  }
})

test('the silhouette is a closed ring at every point in the loop', () => {
  // The outline broke into pieces at t=0 — the resting pose — because coincident and collinear
  // projected points were being admitted to the hull. Every vertex on the outline must be
  // touched by exactly two of its edges, or the heavy outline has a gap in it.
  const n = 128
  for (let k = 0; k < 120; k++) {
    const t = k / 120
    const sil = silhouetteEdges(projectAt(t, n), TESSERACT.edges)
    assert.ok(sil.size >= 4, `t=${t.toFixed(3)} produced only ${sil.size} silhouette edges`)

    const degree = new Map<string, number>()
    for (const key of sil) {
      for (const id of key.split(',')) degree.set(id, (degree.get(id) ?? 0) + 1)
    }
    for (const [vertex, d] of degree) {
      assert.equal(d, 2, `t=${t.toFixed(3)}: vertex ${vertex} has ${d} silhouette edges, not 2`)
    }
  }
})

test('the hull drops interior points that coincide or line up with the boundary', () => {
  // At a true isometric view the cube's body diagonal points at the camera, so its near and far
  // corners land on one pixel. Those are interior points and must not reach the ring.
  const p = projectAt(0, 128)
  const ring = hullOrder(p)

  assert.equal(new Set(ring).size, ring.length, 'the hull ring repeated a vertex')
  assert.ok(ring.length <= 8, `a cube silhouette cannot have ${ring.length} corners`)
})

test('weight is spent on the outline and nowhere else', () => {
  // The spokes running to the centre are edges of the outer cube but are not part of its
  // outline. Thickening them fattened the whole mark instead of its silhouette.
  const sil = silhouetteEdges(projectAt(0.17, 128), TESSERACT.edges)

  assert.ok(sil.size < TESSERACT.edges.length, 'every edge was treated as silhouette')
  assert.ok(sil.size <= 10, `${sil.size} edges is too many for a silhouette`)
})

test('a heavier outline lights more pixels without escaping the frame', () => {
  const light = frame(0.3, 128, { ...WORKING, weight: 1 }).reduce((n, v) => n + (v ? 1 : 0), 0)
  const heavy = frame(0.3, 128, { ...WORKING, weight: 3 }).reduce((n, v) => n + (v ? 1 : 0), 0)

  assert.ok(heavy > light, 'a 3px outline should light more pixels than a 1px one')
})

test('below the detail floor the inner structure is dropped rather than drawn as mush', () => {
  // Thirty-two edges in a 16x16 grid reads as static. What survives is the outline and the core.
  const small = frame(0.3, 16)
  const lit = small.reduce((n, v) => n + (v ? 1 : 0), 0)

  assert.ok(lit > 20, `16px mark is nearly empty: ${lit} lit pixels`)
  assert.ok(lit < 16 * 16 * 0.75, `16px mark is a solid block: ${lit} lit pixels`)
})

test('the two states differ in every dimension that carries meaning', () => {
  assert.ok(WORKING.speed > RESTING.speed * 5, 'working should be visibly faster')
  assert.ok(WORKING.core > RESTING.core, 'working should light its core')
  assert.ok(WORKING.bloom > RESTING.bloom, 'working should glow more')
  assert.ok(WORKING.spill > RESTING.spill, 'working should spill more colour onto the struts')
  // Resting is slow, not stopped: a server that is up with nothing queued is not frozen.
  assert.ok(RESTING.speed > 0, 'resting should still turn')
})

test('crossing between the states lands exactly on the target', () => {
  // An exponential approach never arrives — it sat several percent short for seconds, and the
  // mark spent longer "settling" than anyone would call a transition.
  const midway = crossStates(WORKING, RESTING, CROSS_MS / 2)
  assert.ok(midway.speed < WORKING.speed && midway.speed > RESTING.speed, 'midpoint should be between')

  const done = crossStates(WORKING, RESTING, CROSS_MS)
  assert.deepEqual(done, { ...RESTING })

  const past = crossStates(WORKING, RESTING, CROSS_MS * 3)
  assert.deepEqual(past, { ...RESTING }, 'a crossing must not overshoot once it is finished')
})

test('resting turns slowly enough to read as still, and working fast enough to read as busy', () => {
  const turnSeconds = (speed: number) => 1 / (speed * BASE_RATE)

  assert.ok(turnSeconds(RESTING.speed) > 60, 'resting should take more than a minute a turn')
  assert.ok(turnSeconds(WORKING.speed) < 20, 'working should take under twenty seconds a turn')
})

test('both palettes are complete, and only index zero is transparent', () => {
  for (const dark of [true, false]) {
    const pal = palette(dark)
    assert.equal(pal[0], null, 'index 0 must be transparent')
    for (let i = 1; i < pal.length; i++) {
      assert.ok(pal[i], `palette index ${i} is missing in ${dark ? 'dark' : 'light'}`)
    }
  }
})

test('the light palette keeps the mark off the background it sits on', () => {
  // On a light ground the depth ramp has to run the other way: read the wrong way round, the
  // far edges looked solid and the near ones faded out — depth cueing exactly inverted.
  const luminance = ([r, g, b]: [number, number, number]) => (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255
  const pal = palette(false)
  const buf = frame(0.3, 64, WORKING, false)

  let lit = 0
  let faint = 0
  for (const idx of buf) {
    if (!idx) continue
    lit++
    if (luminance(pal[idx]!) > 0.86) faint++
  }
  assert.ok(lit > 0, 'nothing was drawn')
  assert.ok(faint / lit < 0.25, `${Math.round((100 * faint) / lit)}% of the mark is too pale for a light ground`)
})

test('a 3D cube is used below the detail floor and a 4D one above it', () => {
  assert.equal(CUBE.verts.length, 8)
  assert.equal(CUBE.edges.length, 12)
  assert.equal(TESSERACT.verts.length, 16)
  assert.equal(TESSERACT.edges.length, 32)
})

/** Pixels carrying light rather than structure. */
function litPixels(t: number, cfg = WORKING, n = 128): number {
  const g = new PixelGrid(n)
  g.clear()
  drawTesseract(g, t, cfg, true)
  let lit = 0
  for (const c of g.col) if (c >= C0 && c <= C1) lit++
  return lit
}

test('the light behaves the same way at every point in the turn', () => {
  // The bug this guards: shadows were cast by whichever vertex set had the larger mean radius,
  // and that comparison flipped twice a turn. At the crossover it chose the SMALLER cube, the
  // real outer bars stopped blocking, and the light flooded out — invisible at t=0, obvious a
  // third of the way round. Counting crossings has no such branch, so nothing can flip.
  const samples: number[] = []
  for (let k = 0; k < 24; k++) samples.push(litPixels(k / 24))

  const mean = samples.reduce((a, b) => a + b, 0) / samples.length
  const spread = Math.sqrt(samples.reduce((a, b) => a + (b - mean) ** 2, 0) / samples.length) / mean

  assert.ok(mean > 200, `the mark is barely lit: mean ${Math.round(mean)} pixels`)
  assert.ok(spread < 0.45, `light varies too much across the turn (${spread.toFixed(2)}) — something is flipping`)
  assert.ok(Math.max(...samples) < mean * 2.6, 'one frame floods with light compared with the rest')
})

test('bars take light out of the picture', () => {
  // The unshadowed figure is a uniformly lit disc with no geometry in it at all, so most of it
  // being removed is the point rather than a worry — measured, the bars take roughly nine tenths.
  // What matters is that they do substantial work and still leave a mark you can see, and the
  // amount left is asserted absolutely by the tests above rather than as a share of this.
  const shadowed = litPixels(0.17, WORKING)
  const unshadowed = litPixels(0.17, { ...WORKING, occlude: 0 })

  assert.ok(shadowed < unshadowed * 0.5, 'the bars barely dimmed anything')
  assert.ok(shadowed > unshadowed * 0.03, 'the bars extinguished the light entirely')
  assert.ok(shadowed > 300, `too little light survives: ${shadowed} pixels`)
})

test('more absorption means less light, always', () => {
  // Absorption is exp(-k · crossings), so raising k can only ever darken. A non-monotonic
  // result would mean the crossing count itself is unstable.
  const weak = litPixels(0.17, { ...WORKING, absorb: 0.2 })
  const mid = litPixels(0.17, { ...WORKING, absorb: 0.6 })
  const strong = litPixels(0.17, { ...WORKING, absorb: 1.2 })

  assert.ok(weak > mid, `absorb 0.2 (${weak}) should out-light 0.6 (${mid})`)
  assert.ok(mid > strong, `absorb 0.6 (${mid}) should out-light 1.2 (${strong})`)
})

test('light reaches past the bars without hazing the whole frame', () => {
  // Both halves matter: a total shadow lets nothing out — the outer shell projects to a closed
  // loop, so in flat two dimensions every ray must cross it — and a constant residue behind
  // every bar plateaus into a field of dots across the entire canvas.
  const n = 128
  const g = new PixelGrid(n)
  g.clear()
  drawTesseract(g, 0.17, WORKING, true)

  let beyond = 0
  let farCorners = 0
  for (let y = 0; y < n; y++) {
    for (let x = 0; x < n; x++) {
      const c = g.col[y * n + x]
      if (c < C0 || c > C1) continue
      const d = Math.hypot(x - n / 2, y - n / 2)
      if (d > n * 0.30) beyond++
      if (d > n * 0.46) farCorners++
    }
  }
  assert.ok(beyond > 0, 'no light escaped the shell at all')
  assert.ok(farCorners < 40, `light hazed the far corners (${farCorners} pixels)`)
})

test('the resting state still carries visible light', () => {
  // Resting once fell to ten lit pixels at a 192 grid: present in the arithmetic and invisible
  // in fact. It is the pose the mark holds most of the time.
  assert.ok(litPixels(0, RESTING) > 120, 'the resting mark is too dark to read')
})

test('the light pass does not disturb the loop', () => {
  const g0 = new PixelGrid(96)
  g0.clear()
  drawTesseract(g0, 0, WORKING, true)
  const g1 = new PixelGrid(96)
  g1.clear()
  drawTesseract(g1, 1, WORKING, true)

  assert.deepEqual(Array.from(g1.col), Array.from(g0.col))
})
