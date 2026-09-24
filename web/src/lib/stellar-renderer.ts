import {
  BACKGROUND_SPEED, deepFieldGalaxies, foregroundStars, galaxyPlacement, paintGalaxySprites, paintSkies, SKY_SIZE, travelDirection, type Rgb,
} from './stellar-sky.ts'
import type { StellarMotion } from './stellar-motion'

export type StellarDetail = 'full' | 'reduced' | 'minimal'
type StellarFrame = Pick<StellarMotion, 'time' | 'activity' | 'flow' | 'drift'>

// Photographic detail only reads at sidebar size; the rail and favicons keep the planes,
// edges and the front-corner star, which is what identifies the mark.
export function stellarDetail(size: number): StellarDetail {
  return size >= 192 ? 'full' : size >= 56 ? 'reduced' : 'minimal'
}

// Projected cube axes for the fixed isometric pose, in screen space (y down).
// The front corner (1, 1, 1) lands on the canvas centre.
const AXES = [
  [Math.SQRT1_2, 1 / Math.sqrt(6)],
  [0, -Math.sqrt(2 / 3)],
  [-Math.SQRT1_2, 1 / Math.sqrt(6)],
]
const EDGE = 'rgba(103,232,249,.9)',
  HALO: Rgb = [120, 220, 255]
const ease = (t: number) => t * t * (3 - 2 * t)

export function createStellarRenderer(makeCanvas: () => HTMLCanvasElement = () => document.createElement('canvas')) {
  const canvas = makeCanvas()
  const sprites = paintGalaxySprites(makeCanvas)
  const skies = paintSkies(makeCanvas, sprites)
  const galaxies = deepFieldGalaxies()
  const stars = foregroundStars()
  let disposed = false

  function layout(size: number, fill: number) {
    const scale = (size * fill) / (2 * Math.SQRT2), c = size / 2
    const point = (p: number[]): [number, number] => [
      c + scale * (p[0] * AXES[0][0] + p[1] * AXES[1][0] + p[2] * AXES[2][0]),
      c + scale * (p[0] * AXES[0][1] + p[1] * AXES[1][1] + p[2] * AXES[2][1]),
    ]
    const vector = (axis: number): [number, number] => [2 * scale * AXES[axis][0], 2 * scale * AXES[axis][1]]
    const faces = [0, 1, 2].map((axis) => {
      const u = (axis + 1) % 3, v = (axis + 2) % 3, far = [0, 0, 0]
      far[axis] = 1; far[u] = -1; far[v] = -1
      const origin = point(far), U = vector(u), V = vector(v)
      const corners: [number, number][] = [origin, [origin[0] + U[0], origin[1] + U[1]], [origin[0] + U[0] + V[0], origin[1] + U[1] + V[1]], [origin[0] + V[0], origin[1] + V[1]]]
      return { axis, origin, U, V, corners }
    })
    const rim = [[1, -1, -1], [1, 1, -1], [-1, 1, -1], [-1, 1, 1], [-1, -1, 1], [1, -1, 1]].map(point)
    const inner = [[-1, 1, 1], [1, -1, 1], [1, 1, -1]].map(point)
    return { front: point([1, 1, 1]), faces, rim, inner }
  }

  function outline(ctx: CanvasRenderingContext2D, points: [number, number][]) {
    ctx.beginPath()
    points.forEach(([x, y], i) => (i ? ctx.lineTo(x, y) : ctx.moveTo(x, y)))
    ctx.closePath()
  }

  function star(ctx: CanvasRenderingContext2D, x: number, y: number, reach: number, colour: Rgb, alpha: number, spikes: boolean, bold = false) {
    const halo = ctx.createRadialGradient(x, y, 0, x, y, reach * 0.5)
    halo.addColorStop(0, `rgba(255,255,255,${alpha})`)
    halo.addColorStop(0.07, `rgba(255,255,255,${alpha * 0.9})`)
    halo.addColorStop(0.22, `rgba(${colour},${alpha * 0.32})`)
    halo.addColorStop(1, `rgba(${colour},0)`)
    ctx.fillStyle = halo
    ctx.beginPath()
    ctx.arc(x, y, reach * 0.5, 0, Math.PI * 2)
    ctx.fill()
    if (!spikes) return
    // Tapering diffraction spikes, screen-aligned like every star in a single exposure.
    // Bold spikes survive a browser shrinking the favicon to 16 px.
    const width = Math.max(0.6, reach * (bold ? 0.075 : 0.028))
    for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
      const ray = ctx.createLinearGradient(x, y, x + dx * reach, y + dy * reach)
      ray.addColorStop(0, `rgba(255,255,255,${alpha})`)
      ray.addColorStop(bold ? 0.45 : 0.25, `rgba(${colour},${alpha * (bold ? 0.8 : 0.55)})`)
      ray.addColorStop(1, `rgba(${colour},0)`)
      ctx.fillStyle = ray
      ctx.beginPath()
      ctx.moveTo(x - dy * width, y - dx * width)
      ctx.lineTo(x + dx * reach, y + dy * reach)
      ctx.lineTo(x + dy * width, y + dx * width)
      ctx.closePath()
      ctx.fill()
    }
  }

  function render(motion: StellarFrame, dark: boolean, size: number, detail = stellarDetail(size)) {
    if (disposed) throw new Error('Stellar renderer disposed')
    if (canvas.width !== size || canvas.height !== size) canvas.width = canvas.height = size
    const ctx = canvas.getContext('2d')
    if (!ctx) throw new Error('Stellar graphics unavailable')
    const S = size, act = motion.activity, textured = detail !== 'minimal'
    const g = layout(S, textured ? 0.84 : 0.9)
    ctx.clearRect(0, 0, S, S)

    if (textured) {
      ctx.save()
      outline(ctx, g.rim)
      if (dark) {
        ctx.shadowColor = 'rgba(34,211,238,.4)'
        ctx.shadowBlur = S * 0.08
      } else {
        ctx.shadowColor = 'rgba(8,47,73,.4)'
        ctx.shadowBlur = S * 0.06
        ctx.shadowOffsetY = S * 0.025
      }
      ctx.fillStyle = '#04121c'
      ctx.fill()
      ctx.restore()
    }

    for (const face of g.faces) {
      const sky = skies[face.axis]
      ctx.save()
      outline(ctx, face.corners)
      ctx.clip()
      if (!textured) {
        ctx.fillStyle = `rgb(${sky.mean.map((v) => Math.min(255, v * 1.25))})`
        ctx.fillRect(0, 0, S, S)
        ctx.restore()
        continue
      }
      // The far sky tiles seamlessly and travels slowest; nearer galaxies pass over it faster.
      const N = SKY_SIZE, [du, dv] = travelDirection(face.axis)
      const shift = (d: number) => ((((d * motion.drift * BACKGROUND_SPEED) % 1) + 1) % 1) * N
      const ox = shift(du), oy = shift(dv)
      ctx.save()
      ctx.transform(face.U[0] / N, face.U[1] / N, face.V[0] / N, face.V[1] / N, face.origin[0], face.origin[1])
      for (const x of [ox - N, ox]) for (const y of [oy - N, oy]) ctx.drawImage(sky.canvas, x, y, N, N)
      ctx.restore()
      ctx.save()
      ctx.transform(face.U[0], face.U[1], face.V[0], face.V[1], face.origin[0], face.origin[1])
      ctx.globalCompositeOperation = 'lighter'
      const passing = detail === 'full' ? galaxies[face.axis] : galaxies[face.axis].slice(-4)
      for (const galaxy of passing) {
        const [u, v] = galaxyPlacement(galaxy, motion.drift)
        ctx.save()
        ctx.globalAlpha = 0.7 + 0.3 * (1 - galaxy.depth)
        ctx.translate(u, v)
        ctx.rotate(galaxy.angle)
        ctx.scale(galaxy.size, galaxy.size * galaxy.tilt)
        ctx.drawImage(sprites[galaxy.kind][galaxy.variant], -0.5, -0.5, 1, 1)
        ctx.restore()
      }
      ctx.restore()
      // Light pools toward the front corner, fixed to the face rather than the travelling sky.
      const pool = ctx.createLinearGradient(face.corners[0][0], face.corners[0][1], face.corners[2][0], face.corners[2][1])
      pool.addColorStop(0, 'rgba(0,3,10,.32)')
      pool.addColorStop(1, 'rgba(0,3,10,0)')
      ctx.fillStyle = pool
      ctx.fillRect(0, 0, S, S)
      const px = S / 256
      for (const st of stars[face.axis].slice(0, detail === 'full' ? 26 : 7)) {
        const progress = (st.phase + motion.flow) % 1
        // Home when idle; while working each star travels to the front corner at (1, 1).
        const place = (t: number) => {
          const k = ease(t) * act
          return [st.u + (1 - st.u) * k, st.v + (1 - st.v) * k]
        }
        const onFace = ([u, v]: number[]) => [face.origin[0] + face.U[0] * u + face.V[0] * v, face.origin[1] + face.U[1] * u + face.V[1] * v]
        const [x, y] = onFace(place(progress))
        const alpha = Math.min(1, (0.35 + st.brightness) * (1 - act * progress ** 3) * (0.94 + 0.06 * Math.sin(motion.time * 0.7 + st.shimmer)))
        if (act > 0.05 && progress > 0.04) {
          // Moving stars smear into short trails, like a tracked exposure.
          const [x0, y0] = onFace(place(Math.max(0, progress - 0.06)))
          const trail = ctx.createLinearGradient(x0, y0, x, y)
          trail.addColorStop(0, `rgba(${st.colour},0)`)
          trail.addColorStop(1, `rgba(${st.colour},${alpha * 0.7 * act})`)
          ctx.strokeStyle = trail
          ctx.lineWidth = Math.max(0.8, px * (1 + st.brightness * 1.5))
          ctx.beginPath()
          ctx.moveTo(x0, y0)
          ctx.lineTo(x, y)
          ctx.stroke()
        }
        const enlarge = detail === 'reduced' ? 1.3 : 1
        if (st.brightness > 0.6) star(ctx, x, y, S * (0.03 + 0.07 * st.brightness) * enlarge, st.colour, alpha, detail === 'full')
        else if (st.brightness > 0.2) star(ctx, x, y, S * (0.018 + 0.03 * st.brightness) * enlarge * 1.1, st.colour, alpha, false)
        else {
          const w = px * (1 + st.brightness * 2) * (detail === 'reduced' ? 1.5 : 1)
          ctx.globalAlpha = alpha
          ctx.fillStyle = `rgb(${st.colour})`
          ctx.fillRect(x - w / 2, y - w / 2, w, w)
          ctx.globalAlpha = 1
        }
      }
      ctx.restore()
    }

    ctx.save()
    ctx.lineCap = ctx.lineJoin = 'round'
    ctx.strokeStyle = EDGE
    const lineWidth = Math.max(1, S * (detail === 'full' ? 0.007 : detail === 'reduced' ? 0.013 : 0.032))
    // A wide faint pass under the crisp edge reads as light caught by the glass.
    for (const [width, alpha] of textured ? [[lineWidth * 3.5, 0.18], [lineWidth, 1]] : [[lineWidth, 1]]) {
      ctx.globalAlpha = alpha
      ctx.lineWidth = width
      outline(ctx, g.rim)
      ctx.stroke()
      for (const [x, y] of g.inner) {
        ctx.beginPath()
        ctx.moveTo(g.front[0], g.front[1])
        ctx.lineTo(x, y)
        ctx.stroke()
      }
    }
    ctx.globalAlpha = 1
    if (!dark) {
      outline(ctx, g.rim)
      ctx.strokeStyle = 'rgba(2,20,32,.6)'
      ctx.lineWidth = Math.max(1, S * 0.005)
      ctx.stroke()
    }

    if (act > 0.01 && textured) {
      // A comet traces the outline while work runs.
      const lengths = g.rim.map((p, i) => Math.hypot(g.rim[(i + 1) % 6][0] - p[0], g.rim[(i + 1) % 6][1] - p[1]))
      const total = lengths.reduce((a, b) => a + b)
      const along = (d: number) => {
        d = ((d % total) + total) % total
        for (let i = 0; i < 6; i++) {
          if (d <= lengths[i]) {
            const a = g.rim[i], b = g.rim[(i + 1) % 6], k = d / lengths[i]
            return [a[0] + (b[0] - a[0]) * k, a[1] + (b[1] - a[1]) * k]
          }
          d -= lengths[i]
        }
        return g.rim[0]
      }
      const head = motion.time * total * 0.28
      for (let i = 0; i < 40; i++) {
        const [x, y] = along(head - i * total * 0.0035)
        ctx.globalAlpha = act * (1 - i / 40) ** 1.6
        ctx.fillStyle = i < 3 ? '#ffffff' : `rgb(${HALO})`
        ctx.beginPath()
        ctx.arc(x, y, Math.max(0.8, S * 0.012) * (1 - i / 60), 0, Math.PI * 2)
        ctx.fill()
      }
      ctx.globalAlpha = 1
    }

    const pulse = 1 + act * 0.2 * Math.sin(motion.time * 3.2) ** 2
    star(ctx, g.front[0], g.front[1], S * (detail === 'full' ? 0.25 : detail === 'reduced' ? 0.34 : 0.42) * pulse, HALO, 1, true, !textured)
    ctx.restore()
    return canvas
  }

  return {
    render,
    destroy() {
      disposed = true
      for (const buffer of [canvas, ...skies.map((sky) => sky.canvas), ...Object.values(sprites).flat()]) buffer.width = buffer.height = 1
    },
  }
}
