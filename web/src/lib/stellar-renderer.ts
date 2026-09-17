import { createSoftMask } from './canvas-soft-mask.ts'
import type { StellarMotion } from './stellar-motion'

// Canvas layers are reused across theme/activity changes: the three faces never swap meshes.
export function createStellarRenderer(
  scenes: CanvasImageSource[],
  makeCanvas: () => HTMLCanvasElement = () => document.createElement('canvas'),
) {
  const canvas = makeCanvas()
  const options = { goldLift: 1.2, shadow: 1, softness: 0.65, glow: 0.55 }
  const N = 1024
  let disposed = false,
    pose = { yaw: 0.082, pitch: 0, depth: 1 },
    depth = 1,
    phase = 0,
    lightTime = 0
  const hex = Array.from({ length: 6 }, (_, i) => [
    Math.sin((i * Math.PI) / 3),
    -Math.cos((i * Math.PI) / 3),
  ])
  const sub = (a: number[], b: number[]) => a.map((v, i) => v - b[i]),
    cross = (a: number[], b: number[]) => [
      a[1] * b[2] - a[2] * b[1],
      a[2] * b[0] - a[0] * b[2],
      a[0] * b[1] - a[1] * b[0],
    ],
    norm = (a: number[]) => {
      const m = Math.hypot(...a) || 1
      return a.map((x) => x / m)
    },
    dot = (a: number[], b: number[]) => a.reduce((s, v, i) => s + v * b[i], 0)
  let key = norm([-0.3, -1.3, 1.4])
  const fill = norm([0.85, -0.18, 0.65])
  const modelBuffer = makeCanvas(),
    modelContext = modelBuffer.getContext('2d')!
  const shadowBuffer = makeCanvas(),
    shadowContext = shadowBuffer.getContext('2d')!
  function rotate([x, y, z]: number[], yaw: number, pitch: number) {
    const xx = x * Math.cos(yaw) + z * Math.sin(yaw),
      zz = -x * Math.sin(yaw) + z * Math.cos(yaw)
    return [
      xx,
      y * Math.cos(pitch) - zz * Math.sin(pitch),
      y * Math.sin(pitch) + zz * Math.cos(pitch),
    ]
  }
  function polygon(ctx: CanvasRenderingContext2D, points: number[][]) {
    ctx.beginPath()
    points.forEach((p, i) => (i ? ctx.lineTo(p[0], p[1]) : ctx.moveTo(p[0], p[1])))
    ctx.closePath()
  }
  function geometry() {
    const a = 0.35355339 * depth,
      roll = ((Math.PI / 3) * (1 - depth)) / 2
    const vertices = hex.map(([x, y], i) => [x, y, (i % 2 ? 1 : -1) * a])
    vertices.push([0, 0, 3 * a])
    const { yaw, pitch } = pose
    const rotated = vertices.map(([x, y, z]) =>
      rotate(
        [x * Math.cos(roll) - y * Math.sin(roll), x * Math.sin(roll) + y * Math.cos(roll), z],
        yaw,
        pitch,
      ),
    )
    const planes = [
      [0, 1, 6, 5],
      [5, 6, 3, 4],
      [6, 1, 2, 3],
    ]
      .map((indices, scene) => {
        const v = indices.map((k) => rotated[k])
        let n = norm(cross(sub(v[1], v[0]), sub(v[3], v[0])))
        if (n[2] < 0) n = n.map((x) => -x)
        return { v, n, scene, z: v.reduce((s, p) => s + p[2], 0) / 4 }
      })
      .sort((a, b) => a.z - b.z)
    return { rotated, planes }
  }
  // Bake the photographic toning once. Canvas filters are absent in some browsers.
  const materials = scenes.map((scene, index) => {
    const texture = makeCanvas()
    texture.width = texture.height = N
    const ctx = texture.getContext('2d')!
    ctx.drawImage(scene, 0, 0, N, N)
    const pixels = ctx.getImageData(0, 0, N, N)
    const contrast = 1 - options.softness * 0.28
    const saturation = 1 - options.softness * 0.24
    const brightness = index === 2 ? options.goldLift : 1.065
    for (let i = 0; i < pixels.data.length; i += 4) {
      const luminance = pixels.data[i] * 0.2126 + pixels.data[i + 1] * 0.7152 + pixels.data[i + 2] * 0.0722
      for (let c = 0; c < 3; c++) {
        const colour = luminance + (pixels.data[i + c] - luminance) * saturation
        pixels.data[i + c] = ((colour - 127.5) * contrast + 127.5) * brightness
      }
    }
    ctx.putImageData(pixels, 0, 0)
    return texture
  })

  const softMask = createSoftMask(makeCanvas)
  const softSilhouette = softMask.draw
  function material(
    ctx: CanvasRenderingContext2D,
    points: number[][],
    scene: number,
    shade: number,
  ) {
    ctx.save()
    polygon(ctx, points)
    ctx.clip()
    const [p, a, , b] = points
    ctx.transform(
      (a[0] - p[0]) / N,
      (a[1] - p[1]) / N,
      (b[0] - p[0]) / N,
      (b[1] - p[1]) / N,
      p[0],
      p[1],
    )
    const scale = 3.0,
      travel = (N * (scale - 1)) / 2,
      x = -travel * (1 + Math.sin(phase / 520 + scene * 1.7)),
      y = -travel * (1 + Math.cos(phase / 712 + scene * 2.1))
    ctx.drawImage(materials[scene], x, y, N * scale, N * scale)
    ctx.fillStyle = `rgba(0,5,17,${shade})`
    ctx.fillRect(0, 0, N, N)
    if (scene === 2) {
      ctx.globalCompositeOperation = 'screen'
      ctx.fillStyle = 'rgba(219,155,79,.085)'
      ctx.fillRect(0, 0, N, N)
    }
    ctx.restore()
  }
  function castShadow(
    ctx: CanvasRenderingContext2D,
    g: ReturnType<typeof geometry>,
    w: number,
    h: number,
    r: number,
    cx: number,
    cy: number,
    dark: boolean,
  ) {
    if (shadowBuffer.width !== w || shadowBuffer.height !== h) {
      shadowBuffer.width = w
      shadowBuffer.height = h
    }
    const sc = shadowContext
    sc.clearRect(0, 0, w, h)
    sc.save()
    sc.translate(cx, cy)
    sc.scale(r, r)
    sc.fillStyle = '#020811'
    // Project every face along the same light rays onto one tilted ground plane.
    const ground = 1.52,
      slope = 0.18,
      d = key.map((v) => -v)
    let count = 0,
      height = 0
    const project = (p: number[]) => {
      const heightAbove = ground + slope * p[2] - p[1],
        t = heightAbove / (d[1] - slope * d[2])
      const q = [p[0] + d[0] * t, p[1] + d[1] * t]
      count++
      height += heightAbove
      return q
    }
    for (const f of g.planes) {
      polygon(sc, f.v.map(project))
      sc.fill()
    }
    sc.restore()
    const meanHeight = height / count,
      softness = r * (0.15 + 0.055 * meanHeight)
    softSilhouette(ctx, shadowBuffer, '#132335', softness, (dark ? 0.43 : 0.17) * options.shadow)
    softSilhouette(ctx, shadowBuffer, '#132335', softness * 0.5, (dark ? 0.12 : 0.055) * options.shadow)
  }

  function drawModel(ctx: CanvasRenderingContext2D, g: ReturnType<typeof geometry>) {
    for (const f of g.planes) {
      const points = f.v.map((v) => [v[0], v[1]]),
        diffuse = Math.max(0, dot(f.n, key)),
        bounce = Math.max(0, dot(f.n, fill))
      const exposure = 0.6 + 0.3 * diffuse + 0.12 * bounce + (f.scene === 2 ? 0.13 : 0)
      material(ctx, points, f.scene, Math.max(0.025, Math.min(0.38, 1 - exposure)))
      ctx.save()
      polygon(ctx, points)
      ctx.clip()
      const center = g.rotated[6],
        recess = Math.max(0, -depth)
      if (recess > 0) {
        const ao = ctx.createRadialGradient(center[0], center[1], 0, center[0], center[1], 0.9)
        const strength = (f.scene === 2 ? 0.36 : 0.51) * recess
        ao.addColorStop(0, `rgba(0,4,14,${strength})`)
        ao.addColorStop(0.36, `rgba(0,4,14,${strength * 0.37})`)
        ao.addColorStop(1, 'rgba(0,4,14,0)')
        ctx.fillStyle = ao
        ctx.fillRect(-2, -2, 4, 4)
      }
      // Soft grazing light separates the planes without adding an outline around the object.
      const sheen = ctx.createLinearGradient(-0.9 + Math.sin(lightTime * 0.1) * 0.25, -1, 0.8, 1)
      sheen.addColorStop(0, `rgba(216,235,255,${diffuse * 0.045})`)
      sheen.addColorStop(0.55, 'rgba(216,235,255,0)')
      sheen.addColorStop(1, 'rgba(216,235,255,0)')
      ctx.fillStyle = sheen
      ctx.fillRect(-2, -2, 4, 4)
      ctx.restore()
    }
    const center = g.rotated[6]
    ctx.save()
    ctx.lineCap = 'round'
    ctx.lineJoin = 'round'
    for (const i of [1, 3, 5]) {
      ctx.beginPath()
      ctx.moveTo(center[0], center[1])
      ctx.lineTo(g.rotated[i][0], g.rotated[i][1])
      ctx.lineWidth = 0.005
      ctx.strokeStyle =
        depth < 0 ? `rgba(0,6,17,${-0.26 * depth})` : `rgba(221,234,250,${0.09 * depth})`
      ctx.stroke()
    }
    if (depth < 0) {
      polygon(
        ctx,
        g.rotated.slice(0, 6).map((v) => [v[0], v[1]]),
      )
      ctx.lineWidth = 0.004
      const edge = ctx.createLinearGradient(-1, -1, 1, 1)
      edge.addColorStop(0, `rgba(190,218,241,${-0.24 * depth})`)
      edge.addColorStop(0.55, 'rgba(190,218,241,0)')
      edge.addColorStop(1, `rgba(156,191,218,${-0.09 * depth})`)
      ctx.strokeStyle = edge
      ctx.stroke()
    }
    // A faint, directional rim gives the glow a physical source along the silhouette.
    polygon(
      ctx,
      g.rotated.slice(0, 6).map((v) => [v[0], v[1]]),
    )
    const rim = ctx.createLinearGradient(-0.7, -1, 1, 0.8)
    rim.addColorStop(0, `rgba(218,234,255,${0.18 * options.glow})`)
    rim.addColorStop(0.5, `rgba(209,201,231,${0.06 * options.glow})`)
    rim.addColorStop(1, `rgba(251,211,153,${0.13 * options.glow})`)
    ctx.strokeStyle = rim
    ctx.lineWidth = 0.007
    ctx.stroke()
    ctx.restore()
  }
  function render(motion: StellarMotion, dark: boolean, size: number) {
    if (disposed) throw new Error('Stellar renderer disposed')
    pose = motion.pose
    depth = pose.depth
    phase = motion.phase
    lightTime = motion.lightTime
    key = norm([
      -0.3 + 0.38 * Math.sin(lightTime * 0.11),
      -1.3 + 0.15 * Math.sin(lightTime * 0.07),
      1.4 + 0.22 * Math.sin(lightTime * 0.09),
    ])
    const w = size,
      h = size
    if (canvas.width !== w || canvas.height !== h) {
      canvas.width = w
      canvas.height = h
    }
    const ctx = canvas.getContext('2d')
    if (!ctx) throw new Error('Stellar graphics unavailable')
    ctx.clearRect(0, 0, w, h)
    const r = size * 0.305,
      cx = w / 2 - r * 0.09,
      cy = h * 0.39,
      g = geometry()
    if (dark) {
      ctx.save()
      ctx.translate(cx + r * 0.2, cy + r * 1.3)
      ctx.scale(r * 1.55, r * 0.45)
      const floorLight = ctx.createRadialGradient(0, 0, 0, 0, 0, 1)
      floorLight.addColorStop(0, 'rgba(115,150,178,.085)')
      floorLight.addColorStop(1, 'rgba(115,150,178,0)')
      ctx.fillStyle = floorLight
      ctx.fillRect(-1, -1, 2, 2)
      ctx.restore()
    }
    castShadow(ctx, g, w, h, r, cx, cy, dark)
    if (modelBuffer.width !== w || modelBuffer.height !== h) {
      modelBuffer.width = w
      modelBuffer.height = h
    }
    const mc = modelContext
    mc.clearRect(0, 0, w, h)
    mc.save()
    mc.translate(cx, cy)
    mc.scale(r, r)
    drawModel(mc, g)
    mc.restore()
    // A broad atmosphere and a tighter cool rim use the same mask as the cube.
    ctx.save()
    ctx.globalCompositeOperation = dark ? 'screen' : 'source-over'
    softSilhouette(ctx, modelBuffer, dark ? '#a7c9ef' : '#bacde3', r * 0.23, options.glow * (dark ? 0.18 : 0.12))
    softSilhouette(ctx, modelBuffer, '#d5e4f5', r * 0.075, options.glow * 0.23)
    ctx.restore()
    ctx.drawImage(modelBuffer, 0, 0)
    return canvas
  }
  return {
    render,
    destroy() {
      disposed = true
      softMask.destroy()
      for (const buffer of [canvas, modelBuffer, shadowBuffer, ...materials])
        buffer.width = buffer.height = 1
    },
  }
}
