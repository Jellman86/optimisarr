// The Stellar cube's deep-field sky, generated rather than shipped as photographs.
// Like the Hubble Deep Field it is mostly galaxies, with only a few foreground stars.
// Everything is seeded, so every renderer and the committed stills show the same sky.
export type Rgb = readonly [number, number, number]

export function seeded(seed: number) {
  return () => {
    seed = (seed + 0x6d2b79f5) | 0
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

const smooth = (t: number) => t * t * (3 - 2 * t)

// A periodic lattice: the sky tiles seamlessly, so it can travel across a face without an edge.
function lattice(seed: number, cells: number) {
  const random = seeded(seed)
  const values = Float32Array.from({ length: cells * cells }, random)
  const at = (i: number, j: number) => values[(((j % cells) + cells) % cells) * cells + (((i % cells) + cells) % cells)]
  return (x: number, y: number) => {
    x *= cells
    y *= cells
    const i = Math.floor(x), j = Math.floor(y), u = smooth(x - i), v = smooth(y - j)
    const a = at(i, j), b = at(i + 1, j), c = at(i, j + 1), d = at(i + 1, j + 1)
    return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v
  }
}

function fractal(seed: number) {
  const octaves = [0, 1, 2, 3, 4].map((k) => lattice(seed * 7 + k, 3 * 2 ** k))
  return (x: number, y: number) => {
    let value = 0, amplitude = 0.5, total = 0
    for (const octave of octaves) {
      value += octave(x, y) * amplitude
      total += amplitude
      amplitude *= 0.52
    }
    return value / total
  }
}

// Blackbody colours from O/B through M; faint field stars skew warm, bright ones hot.
const TEMPERATURES: Rgb[] = [
  [155, 176, 255], [170, 191, 255], [202, 215, 255], [248, 247, 255],
  [255, 244, 234], [255, 229, 207], [255, 210, 161], [255, 186, 124],
]
function starColour(random: () => number, bright: boolean) {
  return TEMPERATURES[Math.min(7, Math.floor(random() ** (bright ? 1.8 : 0.7) * 8))]
}

type Emission = readonly [number, number, number, number]
interface FaceSky { base: Rgb; oxygen: Emission; second: Emission; dust: number }
// Indexed by cube axis: 0 is the right face, 1 the top, 2 the left. The bases step down
// from the lit top so the planes separate even where the nebula is faint.
const FACE_SKIES: FaceSky[] = [
  { base: [3, 8, 17], oxygen: [64, 112, 236, 0.3], second: [40, 200, 222, 0.1], dust: 0.8 },
  { base: [38, 98, 132], oxygen: [45, 212, 222, 0.65], second: [80, 120, 255, 0.28], dust: 0.7 },
  { base: [32, 76, 110], oxygen: [40, 176, 214, 0.42], second: [236, 72, 124, 0.26], dust: 0.4 },
]
export const SKY_SIZE = 384
// The nebula is soft, so it is computed coarsely and smoothed up; stars and galaxies are drawn crisp.
const NEBULA_SIZE = 128

// One heading through space, projected into each face, so all three faces agree on the direction of travel.
const HEADING = [0.55, -0.45, 0.7]
export function travelDirection(axis: number): [number, number] {
  return [-HEADING[(axis + 1) % 3], -HEADING[(axis + 2) % 3]]
}
// The nebula and faint background are the farthest layer and move slowest.
export const BACKGROUND_SPEED = 0.03

export type GalaxyKind = 'spiral' | 'elliptical' | 'edge-on' | 'irregular'
const KINDS: GalaxyKind[] = ['spiral', 'elliptical', 'edge-on', 'irregular']
const SPRITE = 96

export interface Galaxy {
  kind: GalaxyKind
  variant: number
  u: number
  v: number
  size: number
  angle: number
  tilt: number
  depth: number
  direction: [number, number]
}

// Nearer galaxies are larger and pass faster: parallax is what makes the field read as travel.
export function deepFieldGalaxies(): Galaxy[][] {
  const random = seeded(909)
  return [0, 1, 2].map((axis) => {
    const kinds: GalaxyKind[] = [...KINDS, 'spiral', 'elliptical', 'spiral', 'irregular', 'spiral']
    for (let i = kinds.length - 1; i > 0; i--) {
      const j = Math.floor(random() * (i + 1))
      ;[kinds[i], kinds[j]] = [kinds[j], kinds[i]]
    }
    return kinds
      .map((kind) => {
        const depth = 0.3 + random() * 0.7
        return {
          kind,
          variant: random() < 0.5 ? 0 : 1,
          u: random(),
          v: random(),
          size: 0.07 + (0.24 * (1 - depth)) / 0.7,
          angle: random() * Math.PI,
          tilt: kind === 'edge-on' ? 1 : kind === 'elliptical' ? 0.55 + random() * 0.45 : 0.35 + random() * 0.65,
          depth,
          direction: travelDirection(axis),
        }
      })
      .sort((a, b) => b.depth - a.depth)
  })
}

// Positions wrap across a band wider than the face, so a galaxy is always off-face when it wraps.
const wrap = (x: number) => ((((x + 0.25) % 1.5) + 1.5) % 1.5) - 0.25
export function galaxyPlacement(galaxy: Galaxy, drift: number): [number, number] {
  const distance = (drift * 0.12) / galaxy.depth
  return [wrap(galaxy.u + galaxy.direction[0] * distance), wrap(galaxy.v + galaxy.direction[1] * distance)]
}

function glow(ctx: CanvasRenderingContext2D, x: number, y: number, radius: number, colour: Rgb, stops: [number, number][]) {
  const gradient = ctx.createRadialGradient(x, y, 0, x, y, radius)
  for (const [offset, alpha] of stops) gradient.addColorStop(offset, `rgba(${colour},${alpha})`)
  ctx.fillStyle = gradient
  ctx.fillRect(x - radius, y - radius, radius * 2, radius * 2)
}

const GALAXY_PAINTERS: Record<GalaxyKind, (ctx: CanvasRenderingContext2D, random: () => number) => void> = {
  elliptical(ctx, random) {
    const hue: Rgb = [[255, 226, 188], [255, 205, 160], [255, 188, 140]][Math.floor(random() * 3)] as unknown as Rgb
    glow(ctx, 0, 0, 46, hue, [[0, 1], [0.06, 0.9], [0.22, 0.4], [0.55, 0.1], [1, 0]])
  },
  spiral(ctx, random) {
    glow(ctx, 0, 0, 46, [150, 180, 255], [[0, 0.5], [0.5, 0.16], [1, 0]])
    for (let i = 0; i < 420; i++) {
      const r = 0.08 + random() ** 0.8 * 0.9
      const theta = (i % 2) * Math.PI + Math.log(r / 0.08) / 0.38 + (random() - 0.5) * 0.5
      const knot = random() < 0.08
      ctx.fillStyle = `rgba(${knot ? '255,140,190' : '175,200,255'},${(knot ? 0.9 : 0.55) * (1 - r * 0.6)})`
      ctx.fillRect(Math.cos(theta) * r * 44 - 0.8, Math.sin(theta) * r * 44 - 0.8, 1.6, 1.6)
    }
    glow(ctx, 0, 0, 12, [255, 226, 188], [[0, 1], [0.4, 0.5], [1, 0]])
  },
  'edge-on'(ctx) {
    ctx.save()
    ctx.scale(1, 0.16)
    glow(ctx, 0, 0, 46, [200, 214, 255], [[0, 0.7], [0.4, 0.35], [1, 0]])
    ctx.restore()
    glow(ctx, 0, 0, 10, [255, 226, 188], [[0, 0.9], [1, 0]])
    // The dust lane of a disc seen side-on.
    ctx.globalCompositeOperation = 'destination-out'
    ctx.fillStyle = 'rgba(0,0,0,.55)'
    ctx.fillRect(-40, -1, 80, 2)
  },
  irregular(ctx, random) {
    for (let i = 0; i < 9; i++) {
      const x = (random() - 0.5) * 34, y = (random() - 0.5) * 26, pink = random() < 0.25
      glow(ctx, x, y, 4 + random() * 9, pink ? [255, 150, 200] : [160, 190, 255], [[0, 0.55], [1, 0]])
    }
  },
}

export type GalaxySprites = Record<GalaxyKind, HTMLCanvasElement[]>

export function paintGalaxySprites(makeCanvas: () => HTMLCanvasElement): GalaxySprites {
  const sprites = {} as GalaxySprites
  KINDS.forEach((kind, k) => {
    sprites[kind] = [0, 1].map((variant) => {
      const canvas = makeCanvas()
      canvas.width = canvas.height = SPRITE
      const ctx = canvas.getContext('2d')!
      ctx.translate(SPRITE / 2, SPRITE / 2)
      ctx.globalCompositeOperation = 'lighter'
      GALAXY_PAINTERS[kind](ctx, seeded(1200 + k * 10 + variant))
      return canvas
    })
  })
  return sprites
}

export interface Sky { canvas: HTMLCanvasElement; mean: Rgb }

function paintNebula(makeCanvas: () => HTMLCanvasElement, face: FaceSky, axis: number) {
  // One wrapped texel of margin, so smoothing across the tile edge samples real neighbours.
  const N = NEBULA_SIZE, M = N + 2, canvas = makeCanvas()
  canvas.width = canvas.height = M
  const ctx = canvas.getContext('2d')!
  const image = ctx.createImageData(M, M), data = image.data
  const warp = fractal(31 + axis), oxygen = fractal(57 + axis), second = fractal(83 + axis), dust = fractal(101 + axis)
  const sum = [0, 0, 0]
  for (let j = -1; j <= N; j++)
    for (let i = -1; i <= N; i++) {
      const u = i / N, v = j / N, w = warp(u, v)
      const glow1 = (Math.max(0, oxygen(u + 0.35 * w, v - 0.25 * w) - 0.34) / 0.66) ** 1.5
      const glow2 = (Math.max(0, second(v + 0.3 * w, u + 0.2 * w) - 0.45) / 0.55) ** 2
      // Only whole-number frequencies keep the tile periodic.
      const lane = Math.min(1, Math.max(0, (dust(2 * u + 0.2 * w, 2 * v) - 0.46) / 0.22))
      const o = ((j + 1) * M + i + 1) * 4, inside = i >= 0 && j >= 0 && i < N && j < N
      for (let c = 0; c < 3; c++) {
        data[o + c] =
          (face.base[c] + face.oxygen[c] * face.oxygen[3] * glow1 * 1.6 + face.second[c] * face.second[3] * glow2 * 1.6) *
          (1 - face.dust * lane * 0.8)
        if (inside) sum[c] += data[o + c]
      }
      data[o + 3] = 255
    }
  ctx.putImageData(image, 0, 0)
  return { canvas, mean: sum.map((v) => Math.round(v / (N * N))) as unknown as Rgb }
}

export function paintSkies(makeCanvas: () => HTMLCanvasElement, sprites: GalaxySprites): Sky[] {
  return FACE_SKIES.map((face, axis) => {
    const nebula = paintNebula(makeCanvas, face, axis)
    const N = SKY_SIZE, canvas = makeCanvas()
    canvas.width = canvas.height = N
    const ctx = canvas.getContext('2d')!
    ctx.imageSmoothingEnabled = true
    ctx.imageSmoothingQuality = 'high'
    ctx.drawImage(nebula.canvas, 1, 1, NEBULA_SIZE, NEBULA_SIZE, 0, 0, N, N)
    nebula.canvas.width = nebula.canvas.height = 1
    // Anything near an edge is drawn again on the opposite side, so the tile stays seamless.
    const tiled = (x: number, y: number, reach: number, draw: (x: number, y: number) => void) => {
      for (const dx of [-N, 0, N]) for (const dy of [-N, 0, N]) {
        const px = x + dx, py = y + dy
        if (px > -reach && px < N + reach && py > -reach && py < N + reach) draw(px, py)
      }
    }
    const random = seeded(700 + axis)
    // Distant galaxies: the faint smudges that fill most of a deep exposure.
    ctx.globalCompositeOperation = 'lighter'
    for (let g = 0; g < 70; g++) {
      const kind = KINDS[Math.floor(random() * 4)], sprite = sprites[kind][random() < 0.5 ? 0 : 1]
      const size = 4 + random() ** 2 * 12, angle = random() * Math.PI, tilt = 0.35 + random() * 0.65
      const alpha = 0.35 + random() * 0.5
      tiled(random() * N, random() * N, size, (x, y) => {
        ctx.save()
        ctx.globalAlpha = alpha
        ctx.translate(x, y)
        ctx.rotate(angle)
        ctx.scale(size, size * (kind === 'edge-on' ? 1 : tilt))
        ctx.drawImage(sprite, -0.5, -0.5, 1, 1)
        ctx.restore()
      })
    }
    ctx.globalCompositeOperation = 'source-over'
    // Unresolved background stars: mostly faint and warm, a few with a soft halo.
    for (let s = 0; s < 700; s++) {
      const brightness = random() ** 5, colour = starColour(random, brightness > 0.4)
      const width = brightness > 0.5 ? 1.6 : brightness > 0.15 ? 1.2 : 0.9
      tiled(random() * N, random() * N, 8, (x, y) => {
        ctx.globalAlpha = 0.18 + 0.82 * Math.min(1, brightness * 1.6)
        ctx.fillStyle = `rgb(${colour})`
        ctx.fillRect(x - width / 2, y - width / 2, width, width)
        ctx.globalAlpha = 1
        if (brightness > 0.45) glow(ctx, x, y, 3 + brightness * 4, colour, [[0, 0.35 * brightness], [1, 0]])
      })
    }
    return { canvas, mean: nebula.mean }
  })
}

export interface ForegroundStar { u: number; v: number; brightness: number; colour: Rgb; phase: number; shimmer: number }

// Foreground stars that stream while working; brightness follows a power law, brightest first.
export function foregroundStars(): ForegroundStar[][] {
  const random = seeded(404)
  return [0, 1, 2].map(() =>
    Array.from({ length: 26 }, () => {
      const brightness = random() ** 2.6
      return { u: 0.06 + random() * 0.84, v: 0.06 + random() * 0.84, brightness, colour: starColour(random, brightness > 0.35), phase: random(), shimmer: random() * Math.PI * 2 }
    }).sort((a, b) => b.brightness - a.brightness),
  )
}
