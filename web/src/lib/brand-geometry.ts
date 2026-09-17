import { add, sub, mul, dot, cross, norm, mean } from './brand-math.ts'
function cleanPolygon(points: number[][]) {
  let p = points.filter(
    (a, i) => Math.hypot(...sub(a, points[(i + points.length - 1) % points.length])) > 1e-8,
  )
  let changed = true
  while (changed && p.length > 3) {
    changed = false
    for (let i = 0; i < p.length; i++) {
      const before = p[(i + p.length - 1) % p.length],
        at = p[i],
        after = p[(i + 1) % p.length]
      if (Math.hypot(...cross(sub(at, before), sub(after, at))) < 1e-10) {
        p.splice(i, 1)
        changed = true
        break
      }
    }
  }
  return p
}
function clip(faces: number[][][], n: number[], d: number) {
  const out: number[][][] = [],
    cap: number[][] = [],
    epsilon = 1e-9
  function addCap(p: number[]) {
    if (!cap.some((q) => Math.hypot(...sub(q, p)) < 1e-8)) cap.push(p)
  }
  for (const f of faces) {
    const polygon = []
    for (let i = 0; i < f.length; i++) {
      const a = f[i],
        b = f[(i + 1) % f.length]
      let da = dot(n, a) - d,
        db = dot(n, b) - d
      if (Math.abs(da) < epsilon) da = 0
      if (Math.abs(db) < epsilon) db = 0
      if (da <= 0) polygon.push(a)
      if (da === 0) addCap(a)
      if (da * db < 0) {
        const p = add(a, mul(sub(b, a), da / (da - db)))
        polygon.push(p)
        addCap(p)
      }
    }
    const p = cleanPolygon(polygon)
    if (p.length >= 3) out.push(p)
  }
  if (cap.length >= 3) {
    const c = mean(cap),
      u = norm(cross(n, Math.abs(n[1]) < 0.9 ? [0, 1, 0] : [1, 0, 0])),
      v = cross(n, u)
    cap.sort(
      (a, b) =>
        Math.atan2(dot(sub(a, c), v), dot(sub(a, c), u)) - Math.atan2(dot(sub(b, c), v), dot(sub(b, c), u)),
    )
    const p = cleanPolygon(cap)
    if (p.length >= 3) out.push(p)
  }
  return out
}

const vertices = [
  [-1, -1, -1],
  [1, -1, -1],
  [1, 1, -1],
  [-1, 1, -1],
  [-1, -1, 1],
  [1, -1, 1],
  [1, 1, 1],
  [-1, 1, 1],
].map((p) => [
  (p[0] - p[2]) / Math.SQRT2,
  (p[0] + p[1] + p[2]) / Math.sqrt(3),
  (p[0] - 2 * p[1] + p[2]) / Math.sqrt(6),
])
const indices = [
  [0, 1, 2, 3],
  [4, 7, 6, 5],
  [0, 4, 5, 1],
  [3, 2, 6, 7],
  [0, 3, 7, 4],
  [1, 5, 6, 2],
]
function orient(faces: number[][][]) {
  const c = mean(faces.flat())
  return faces.map((p) =>
    dot(cross(sub(p[1], p[0]), sub(p[2], p[0])), sub(mean(p), c)) < 0 ? [...p].reverse() : p,
  )
}
function bevel(faces: number[][][], width: number) {
  faces = orient(faces)
  const planes: [number[], number][] = [],
    edges = new Map<string, { n: number[] }>()
  for (const f of faces) {
    const n = norm(cross(sub(f[1], f[0]), sub(f[2], f[0])))
    for (let i = 0; i < f.length; i++) {
      const a = f[i],
        b = f[(i + 1) % f.length],
        key = [a.map((x) => x.toFixed(5)).join(','), b.map((x) => x.toFixed(5)).join(',')].sort().join('|')
      if (edges.has(key)) {
        const old = edges.get(key),
          bisector = norm(add(n, old!.n))
        if (Math.hypot(...bisector) > 0.1) planes.push([bisector, dot(bisector, a) - width])
      } else edges.set(key, { n })
    }
  }
  for (const [n, d] of planes) faces = clip(faces, n, d)
  return orient(faces)
}

function triangulate(faces: number[][][]) {
  const data: number[] = []
  for (const face of orient(faces)) {
    const normal = norm(cross(sub(face[1], face[0]), sub(face[2], face[0])))
    for (let j = 1; j < face.length - 1; j++) {
      for (const point of [face[0], face[j], face[j + 1]]) data.push(...point, ...normal)
    }
  }
  return new Float32Array(data)
}

export function createBrandGeometry() {
  // Bevel the original shell before cutting so the pieces share exact boundaries at rest.
  const shell = bevel(
    indices.map((face) => face.map((j) => vertices[j])),
    0.022,
  )
  const radius = Math.sqrt(3)
  const slices = Array.from({ length: 15 }, (_, i) => {
    const low = -radius + (i * 2 * radius) / 15
    const high = -radius + ((i + 1) * 2 * radius) / 15
    return triangulate(clip(clip(shell, [0, 1, 0], high), [0, -1, 0], -low))
  })
  const floor = triangulate([
    [
      [-5, -1.72, -5],
      [-5, -1.72, 5],
      [5, -1.72, 5],
      [5, -1.72, -5],
    ],
  ])
  return { slices, shell: triangulate(shell), floor }
}
