// The application mark: a four-dimensional hypercube, rasterised onto a pixel grid.
//
// Deliberately free of the DOM. Everything here takes numbers and returns numbers, so the
// geometry — which is the part with the interesting failure modes — is unit tested without a
// canvas, and the Svelte component is left with nothing to do but copy an index buffer onto a
// 2D context.

export type TesseractState = {
  /** Turns per loop multiplier. 1 is roughly 26 seconds. */
  speed: number
  /** Brightness of the core disc. Zero leaves the bloom and shafts to carry the light. */
  core: number
  /** Length of the rays cast toward the inner cube's corners. */
  shaft: number
  /** Radius of the dithered halo behind everything. */
  bloom: number
  /** How far the core's colour reaches onto the struts. */
  spill: number
  /** Silhouette thickness in pixels. Only the outline is drawn heavy. */
  weight: number
  /** Whether the bars cast shadows through the light. Eased, so it is a number, not a flag. */
  occlude: number
  /** How much of the light one bar takes. Low fills the lattice and spills out of it. */
  absorb: number
}

/**
 * The mark's two looks. Which one it wears is the server's state, not a preference: it wakes
 * when there is work and settles when the queue goes quiet.
 */
export const WORKING: TesseractState =
  { speed: 2.5, core: 0.3, shaft: 0.9, bloom: 0.3, spill: 0.7, weight: 3, occlude: 1, absorb: 0.45 }
export const RESTING: TesseractState =
  { speed: 0.3, core: 0.06, shaft: 0.7, bloom: 0.14, spill: 0.1, weight: 3, occlude: 1, absorb: 0.45 }

/** Resting is slow, not stopped: a server that is up with nothing queued is not frozen. */
export const BASE_RATE = 0.038
/** How long the two states take to cross. Long enough to notice, short enough not to wait for. */
export const CROSS_MS = 1400

export const EASED_KEYS = ['speed', 'core', 'shaft', 'bloom', 'spill', 'weight', 'occlude', 'absorb'] as const

const easeInOut = (u: number) => (u < 0.5 ? 2 * u * u : 1 - Math.pow(-2 * u + 2, 2) / 2)

/**
 * A fixed-duration crossing rather than an exponential approach. An exponential never actually
 * arrives — it sat several percent short of its target for seconds — so the mark spent longer
 * "settling" than anyone would call a transition.
 */
export function crossStates(from: TesseractState, to: TesseractState, elapsedMs: number): TesseractState {
  const u = Math.max(0, Math.min(1, elapsedMs / CROSS_MS))
  // A finished crossing is the target, bit for bit. Interpolating at u = 1 leaves floating
  // point dust — 0.2999999999999998 rather than 0.3 — so the mark would never actually settle
  // on the values it is specified to rest at.
  if (u >= 1) return { ...to }
  const e = easeInOut(u)
  const out = {} as TesseractState
  for (const key of EASED_KEYS) out[key] = from[key] + (to[key] - from[key]) * e
  return out
}

// ── Palette ───────────────────────────────────────────────────────────────
// Index semantics are identical in both themes: low means least visible against the ground,
// high means most. On a dark ground that makes the near edge the palest step; on a light one
// the darkest. Keeping the meaning constant is what lets one renderer serve both.

const STRUCT = [
  '#101a29', '#16202f', '#1d293d', '#273449', '#314158', '#3b4a61', '#45556c',
  '#576480', '#62748e', '#7c8ba5', '#90a1b9', '#aab8cc', '#cad5e2', '#e2e9f2',
]
const CORE_DARK = [
  '#03202c', '#04384a', '#005f78', '#00759a', '#0092b8', '#00a3c7', '#00b8db', '#00c6e8',
  '#00d3f2', '#3adcf7', '#6ae5fa', '#95edfc', '#bdf5fe', '#dcfbff', '#f2feff',
]
const CORE_LIGHT = [
  '#cfe9f4', '#b9e1f1', '#a1d7ed', '#88cce8', '#6fc0e2', '#56b2da', '#3fa4d1', '#2b95c6',
  '#1a86b9', '#0c77aa', '#03689a', '#005a89', '#004c76', '#003f63', '#003352',
]

const CYAN = [0, 211, 242]
const CYAN_L = 0.2126 * CYAN[0] + 0.7152 * CYAN[1] + 0.0722 * CYAN[2]

/**
 * The spill ramp is the structural ramp with its hue turned toward cyan and its luminance left
 * alone, so spill changes the colour of a strut and never its brightness — which is what light
 * landing on something does. Two separately authored ramps drifted apart in the middle and the
 * spill came out as speckle.
 */
function tintToCyan(hex: string, strength: number): string {
  const r = parseInt(hex.slice(1, 3), 16)
  const g = parseInt(hex.slice(3, 5), 16)
  const b = parseInt(hex.slice(5, 7), 16)
  const l = 0.2126 * r + 0.7152 * g + 0.0722 * b
  const k = l / CYAN_L
  const mix = (a: number, c: number) => Math.round(Math.max(0, Math.min(255, a + (c * k - a) * strength)))
  return '#' + [mix(r, CYAN[0]), mix(g, CYAN[1]), mix(b, CYAN[2])].map((c) => c.toString(16).padStart(2, '0')).join('')
}

const SPILLED = STRUCT.map((h) => tintToCyan(h, 0.8))

export const S0 = 1
export const S1 = S0 + STRUCT.length - 1
export const P0 = S1 + 1
export const C0 = P0 + STRUCT.length
export const C1 = C0 + CORE_DARK.length - 1

function toRgb(list: readonly string[]): Array<[number, number, number]> {
  return list.map((h) => [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16)])
}

/** Index 0 is transparent; everything after it is opaque. */
export function palette(dark: boolean): Array<[number, number, number] | null> {
  return [null, ...toRgb(STRUCT), ...toRgb(SPILLED), ...toRgb(dark ? CORE_DARK : CORE_LIGHT)]
}

// ── Geometry ──────────────────────────────────────────────────────────────

export type Vec4 = [number, number, number, number]
export type Projected = { x: number; y: number; d: number; z: number }

export function rot(v: Vec4, a: number, b: number, theta: number): void {
  const c = Math.cos(theta)
  const s = Math.sin(theta)
  const va = v[a]
  const vb = v[b]
  v[a] = va * c - vb * s
  v[b] = va * s + vb * c
}

const D4 = 2.9
const D3 = 3.6
export const CENTRE_DEPTH = (1 / D4) * (1 / D3)

/**
 * Two real perspective divides. `d` orders the depth buffer and uses both; `z` drives the
 * shading and uses the 3D divide alone — how far a strut lies along w is not a reason to draw
 * it dark, and doing so made the inner cube vanish into the core at rest.
 */
export function project(v: Vec4, n: number, scale: number): Projected {
  const k4 = 1 / (D4 - v[3])
  const x3 = v[0] * k4
  const y3 = v[1] * k4
  const z3 = v[2] * k4
  const k3 = 1 / (D3 - z3)
  return { x: n / 2 + x3 * k3 * scale, y: n / 2 + y3 * k3 * scale, d: k3 * k4, z: k3 }
}

/**
 * The scale that makes these vertices span `fill` of the grid after both divides. Measured
 * rather than guessed: the projection's shrink factor changes with every rotation, and a fixed
 * scale left the solid a sixth of the size it should have been.
 */
export function fitScale(verts: readonly Vec4[], n: number, fill: number): number {
  let maxr = 1e-6
  for (const v of verts) {
    const k4 = 1 / (D4 - v[3])
    const k3 = 1 / (D3 - v[2] * k4)
    maxr = Math.max(maxr, Math.abs(v[0] * k4 * k3), Math.abs(v[1] * k4 * k3))
  }
  return ((n / 2) * fill) / maxr
}

export function hypercube(dims: number): { verts: Vec4[]; edges: Array<[number, number]> } {
  const verts: Vec4[] = []
  const edges: Array<[number, number]> = []
  for (let i = 0; i < 1 << dims; i++) {
    const v = [0, 0, 0, 0] as Vec4
    for (let b = 0; b < dims; b++) v[b] = (i >> b) & 1 ? 1 : -1
    verts.push(v)
    for (let b = 0; b < dims; b++) {
      const j = i ^ (1 << b)
      if (j > i) edges.push([i, j])
    }
  }
  return { verts, edges }
}

export const TESSERACT = hypercube(4)
export const CUBE = hypercube(3)

/**
 * Convex hull of the projected vertices, by monotone chain. The silhouette of a convex solid
 * is exactly the boundary of its projected points' hull, so this is what "the outline" means
 * at any angle — and it stays right through the tesseract turning inside out.
 *
 * Two degeneracies have to be handled, and both occur at the resting pose rather than somewhere
 * obscure. Coincident points: at a true isometric view the body diagonal points at the camera,
 * so near and far corners land on one pixel and four vertices stack on the centre; they are
 * interior, so they are deduplicated away first. Collinear points: keeping them looks safe and
 * is not, because it admits interior points that merely line up with the boundary — which
 * produced a ring with duplicate entries and an outline in pieces.
 */
export function hullOrder(pts: readonly Projected[]): number[] {
  const seen = new Map<string, { x: number; y: number; i: number }>()
  for (let i = 0; i < pts.length; i++) {
    const key = `${Math.round(pts[i].x * 8)}:${Math.round(pts[i].y * 8)}`
    if (!seen.has(key)) seen.set(key, { x: pts[i].x, y: pts[i].y, i })
  }
  const p = [...seen.values()].sort((a, b) => a.x - b.x || a.y - b.y)
  if (p.length < 3) return p.map((q) => q.i)
  const cross = (o: typeof p[0], a: typeof p[0], b: typeof p[0]) =>
    (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x)
  const build = (src: typeof p) => {
    const out: typeof p = []
    for (const q of src) {
      while (out.length >= 2 && cross(out[out.length - 2], out[out.length - 1], q) <= 0) out.pop()
      out.push(q)
    }
    out.pop()
    return out
  }
  return [...build(p), ...build([...p].reverse())].map((q) => q.i)
}

const edgeKey = (a: number, b: number) => `${Math.min(a, b)},${Math.max(a, b)}`

/** The edges of the solid that lie on its silhouette. Recomputed per frame. */
export function silhouetteEdges(
  pts: readonly Projected[],
  edges: ReadonlyArray<readonly [number, number]>,
): Set<string> {
  const ring = hullOrder(pts)
  const onHull = new Set<string>()
  for (let i = 0; i < ring.length; i++) onHull.add(edgeKey(ring[i], ring[(i + 1) % ring.length]))
  // A hull span with no edge of the solid behind it is a gap in the point set, not a line.
  const out = new Set<string>()
  for (const e of edges) {
    const key = edgeKey(e[0], e[1])
    if (onHull.has(key)) out.add(key)
  }
  return out
}

// ── Rasteriser ────────────────────────────────────────────────────────────

export class PixelGrid {
  readonly n: number
  readonly col: Uint8Array
  private readonly dep: Float32Array

  constructor(n: number) {
    this.n = n
    this.col = new Uint8Array(n * n)
    this.dep = new Float32Array(n * n)
  }

  clear(): void {
    this.col.fill(0)
    this.dep.fill(-1e9)
  }

  inside(x: number, y: number): boolean {
    return x >= 0 && y >= 0 && x < this.n && y < this.n
  }

  px(x: number, y: number, c: number, d: number): void {
    const xi = Math.round(x)
    const yi = Math.round(y)
    if (!this.inside(xi, yi) || !c) return
    const i = yi * this.n + xi
    if (d >= this.dep[i]) {
      this.col[i] = c
      this.dep[i] = d
    }
  }

  /**
   * Stamp a brush of `w` pixels. An explicit offset list rather than a radius test, so the
   * shape at each weight is deliberate: a 2x2 block, then a 3x3 with the corners removed,
   * which thickens a diagonal without turning it into a staircase of squares.
   */
  private stamp(x: number, y: number, c: number, d: number, w: number): void {
    if (w <= 1) {
      this.px(x, y, c, d)
      return
    }
    const offs = w === 2
      ? [[0, 0], [1, 0], [0, 1], [1, 1]]
      : [[0, 0], [1, 0], [-1, 0], [0, 1], [0, -1], [1, 1], [-1, -1], [1, -1], [-1, 1]]
    for (const [ox, oy] of offs) this.px(x + ox, y + oy, c, d)
  }

  line(
    x0: number, y0: number, x1: number, y1: number,
    shade: (u: number) => number,
    depth: (u: number) => number,
    weight = 1,
  ): void {
    let x = Math.round(x0)
    let y = Math.round(y0)
    const xe = Math.round(x1)
    const ye = Math.round(y1)
    const dx = Math.abs(xe - x)
    const sx = x < xe ? 1 : -1
    const dy = -Math.abs(ye - y)
    const sy = y < ye ? 1 : -1
    const steps = Math.max(dx, -dy) || 1
    let err = dx + dy
    let i = 0
    for (;;) {
      const u = i / steps
      this.stamp(x, y, shade(u), depth(u), weight)
      if (x === xe && y === ye) break
      const e2 = 2 * err
      if (e2 >= dy) { err += dy; x += sx }
      if (e2 <= dx) { err += dx; y += sy }
      i++
    }
  }
}

// Ordered dither, so the shafts and the spill fade out without a hard edge.
const BAYER = [[0, 8, 2, 10], [12, 4, 14, 6], [3, 11, 1, 9], [15, 7, 13, 5]]
const dither = (x: number, y: number, t: number) =>
  t > (BAYER[((y % 4) + 4) % 4][((x % 4) + 4) % 4] + 0.5) / 16

/** Below this the inner cube and its connecting edges are dropped; the outline is all there is room for. */
const SIMPLE_BELOW = 28

const HOME_TURN = Math.PI / 4
const HOME_TILT = Math.atan(Math.SQRT1_2)
const TAU = Math.PI * 2

/**
 * Draw one frame at loop position `t` in 0..1.
 *
 * Every rotation is a whole number of turns across the loop, so the frame at t = 1 is the frame
 * at t = 0 exactly — anything fractional leaves the solid in a different pose at the seam and
 * the mark visibly jumps. The resting pose is t = 0, put on a true isometric view because that
 * is the frame the icon spends most of its life showing.
 */
export function drawTesseract(g: PixelGrid, t: number, cfg: TesseractState, dark = true): void {
  const n = g.n
  const simple = n < SIMPLE_BELOW
  const cx = n / 2
  const cy = n / 2
  const src = simple ? CUBE : TESSERACT

  const xw = t * TAU
  const yz = t * TAU * 2
  const xz = t * TAU
  const verts = src.verts.map((v) => {
    const c = [...v] as Vec4
    if (!simple) {
      rot(c, 0, 3, xw)
      rot(c, 1, 2, yz)
    }
    rot(c, 0, 2, xz + HOME_TURN)
    rot(c, 1, 2, HOME_TILT)
    return c
  })

  const fill = (simple ? 0.86 : 0.74) - (cfg.weight - 1) * 0.03
  const s = fitScale(verts, n, fill)
  const p = verts.map((v) => project(v, n, s))
  let lo = Infinity
  let hi = -Infinity
  for (const q of p) {
    lo = Math.min(lo, q.z)
    hi = Math.max(hi, q.z)
  }

  const coreR = Math.max(1.05, n * 0.055 * cfg.core * (0.86 + 0.14 * (Math.sin(t * TAU * 2) + 1) / 2))

  // Pass 1 · bloom, laid down first so every later pass composites over it.
  if (cfg.bloom > 0) {
    const r = n * 0.4 * cfg.bloom
    for (let y = Math.max(0, Math.floor(cy - r)); y <= Math.min(n - 1, Math.ceil(cy + r)); y++) {
      for (let x = Math.max(0, Math.floor(cx - r)); x <= Math.min(n - 1, Math.ceil(cx + r)); x++) {
        const dist = Math.hypot(x - cx, y - cy)
        if (dist > r) continue
        const f = Math.pow(1 - dist / r, 2.6)
        if (!dither(x, y, f * 1.5)) continue
        g.px(x, y, Math.min(C0 + 5, C0 + Math.round(f * 4.5)), -1e8)
      }
    }
  }

  // Pass 2 · wireframe, far to near so the depth buffer resolves crossings cleanly. Depth runs
  // toward whichever end of the ramp contrasts with the ground: read the wrong way round, a
  // light background made the far edges look solid and the near ones fade.
  const far = dark ? (n < 24 ? S0 + 8 : n < 40 ? S0 + 4 : S0) : S1 - 2
  const near = dark ? S1 : n < 24 ? S0 + 1 : S0
  const shade = (z: number) => {
    const u = hi === lo ? 0.5 : Math.max(0, Math.min(1, (z - lo) / (hi - lo)))
    return Math.round(far + u * (near - far))
  }
  // Outline weight is capped by how many pixels there are to spend. Three pixels is right on a
  // 96 grid and a tenth of the width on a 32 one, where it closes the mark up into a blob.
  const heavy = Math.max(1, Math.min(Math.round(cfg.weight), Math.floor(n / 32)))
  const silhouette = heavy > 1 ? silhouetteEdges(p, src.edges) : null

  const order = src.edges
    .map((e) => ({ e, d: (p[e[0]].d + p[e[1]].d) / 2 }))
    .sort((a, b) => a.d - b.d)
  for (const { e } of order) {
    const a = p[e[0]]
    const b = p[e[1]]
    const w = silhouette?.has(edgeKey(e[0], e[1])) ? heavy : 1
    g.line(
      a.x, a.y, b.x, b.y,
      (u) => shade(a.z + (b.z - a.z) * u),
      (u) => a.d + (b.d - a.d) * u,
      w,
    )
  }
  if (n >= 48) {
    const bump = dark ? 2 : -2
    for (const q of p) g.px(q.x, q.y, Math.max(S0, Math.min(S1, shade(q.z) + bump)), q.d + 0.002)
  }

  // Pass 3 · light through the solid.
  //
  // Every bar absorbs. Not a chosen shell: picking eight of the sixteen vertices as "the outer
  // cube" and blocking with those alone flips twice a turn — at the crossover the smaller cube
  // wins the comparison, the real outer bars stop blocking, and the light floods out. Some bars
  // blocking while others did not was exactly that.
  //
  // So this counts CROSSINGS: for every angle and radius, how many bars the light passed through
  // to get there. Brightness is exp(-absorb · crossings), which is absorption — deep in the
  // lattice where rays cross several bars it is dim, out through a gap it is bright, and nothing
  // depends on deciding which cube is inside the other, so nothing can flip.
  //
  // It has to run after the wireframe, because the wireframe is what it reads as the blockers.
  if (cfg.shaft > 0) {
    const ANG = 360
    const RAD = 72
    const maxR = Math.hypot(n, n) / 2
    const crossings = new Uint8Array(ANG * RAD)
    const bucketOf = (a: number) => {
      const b = Math.floor(((a + Math.PI) / (Math.PI * 2)) * ANG) % ANG
      return b < 0 ? b + ANG : b
    }

    for (let y = 0; y < n; y++) {
      for (let x = 0; x < n; x++) {
        const c = g.col[y * n + x]
        if (c < S0 || c > S1) continue
        const dx = x - cx
        const dy = y - cy
        const d = Math.hypot(dx, dy)
        if (d < 1) continue
        const bin = Math.min(RAD - 1, Math.floor((d / maxR) * RAD))
        const spread = Math.max(1, Math.ceil((Math.atan2(1, d) / (Math.PI * 2)) * ANG))
        const b0 = bucketOf(Math.atan2(dy, dx))
        // One bar counts once per cell, however many of its pixels land there.
        for (let k = -spread; k <= spread; k++) crossings[((b0 + k + ANG) % ANG) * RAD + bin] = 1
      }
    }
    // Running total outward: how many bars the light met on the way to each radius.
    for (let a = 0; a < ANG; a++) {
      let run = 0
      for (let r = 0; r < RAD; r++) {
        run += crossings[a * RAD + r]
        crossings[a * RAD + r] = Math.min(255, run)
      }
    }

    const reach = n * 0.66 * cfg.shaft
    for (let y = 0; y < n; y++) {
      for (let x = 0; x < n; x++) {
        const i = y * n + x
        const c = g.col[i]
        if (c >= S0 && c <= S1) continue // struts keep their own colour
        const dx = x - cx
        const dy = y - cy
        const d = Math.hypot(dx, dy)
        if (d > reach || d < 1) continue

        // Sampled across a spread of angles. A single lookup makes every pixel either lit or
        // shadowed, so the boundary is a straight line and the lit region reads as a flat slab.
        // Averaging is what a penumbra is: near a shadow edge some samples clear the bar and
        // some do not, so the light falls away instead of stopping.
        let shade = 1
        if (cfg.occlude > 0) {
          const bin = Math.min(RAD - 1, Math.floor((d / maxR) * RAD))
          const b0 = bucketOf(Math.atan2(dy, dx))
          const SAMPLES = 7
          const SPAN = 5
          let sum = 0
          for (let k = 0; k < SAMPLES; k++) {
            const off = Math.round((k / (SAMPLES - 1) - 0.5) * 2 * SPAN)
            sum += Math.exp(-cfg.absorb * crossings[((b0 + off + ANG) % ANG) * RAD + bin])
          }
          // Blended rather than switched, so a crossing that eases occlude opens the shadows
          // gradually instead of snapping them on at the halfway point.
          shade = 1 + (sum / SAMPLES - 1) * Math.min(1, cfg.occlude)
        }

        const f = Math.pow(1 - d / reach, 1.6) * shade
        if (!dither(x, y, f * 2.6)) continue
        g.px(x, y, Math.min(C0 + 8, C0 + 1 + Math.round(f * 7)), -9e7 + (reach - d) * 1e-4)
      }
    }
  }

  // Pass 4 · spill. Remaps structural pixels onto the cyan-tinted ramp at matching brightness.
  if (cfg.spill > 0) {
    const r = n * 0.42 * cfg.spill
    for (let y = 0; y < n; y++) {
      for (let x = 0; x < n; x++) {
        const i = y * n + x
        const c = g.col[i]
        if (c < S0 || c > S1) continue
        const dist = Math.hypot(x - cx, y - cy)
        if (dist > r) continue
        if (!dither(x, y, Math.pow(1 - dist / r, 1.5) * 1.35)) continue
        g.col[i] = P0 + (c - S0)
      }
    }
  }

  // Pass 5 · core, above everything, with a long falloff so the centre reads as a source.
  if (cfg.core > 0) {
    const outer = coreR * 2.3
    for (let y = Math.max(0, Math.floor(cy - outer)); y <= Math.min(n - 1, Math.ceil(cy + outer)); y++) {
      for (let x = Math.max(0, Math.floor(cx - outer)); x <= Math.min(n - 1, Math.ceil(cx + outer)); x++) {
        const dist = Math.hypot(x - cx, y - cy)
        if (dist > outer) continue
        const f = Math.pow(Math.max(0, 1 - dist / outer), 2.2)
        if (dist > coreR && !dither(x, y, f * 1.6)) continue
        g.px(x, y, Math.max(C0, Math.min(C1, Math.round(C0 + 3 + f * (C1 - C0 - 3)))), 1e6 + f)
      }
    }
  }
}

/** Paint an index buffer into RGBA bytes. Index 0 is transparent — the mark has an alpha channel. */
export function toImageBytes(g: PixelGrid, out: Uint8ClampedArray, pal: ReturnType<typeof palette>): void {
  for (let i = 0; i < g.col.length; i++) {
    const rgb = pal[g.col[i]]
    if (!rgb) {
      out[i * 4 + 3] = 0
      continue
    }
    out[i * 4] = rgb[0]
    out[i * 4 + 1] = rgb[1]
    out[i * 4 + 2] = rgb[2]
    out[i * 4 + 3] = 255
  }
}
