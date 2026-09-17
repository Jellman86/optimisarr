export const add = (a: number[], b: number[]) => a.map((x, i) => x + b[i]),
  sub = (a: number[], b: number[]) => a.map((x, i) => x - b[i]),
  mul = (a: number[], s: number) => a.map((x) => x * s),
  dot = (a: number[], b: number[]) => a.reduce((v, x, i) => v + x * b[i], 0)
export const cross = (a: number[], b: number[]) => [
    a[1] * b[2] - a[2] * b[1],
    a[2] * b[0] - a[0] * b[2],
    a[0] * b[1] - a[1] * b[0],
  ],
  norm = (a: number[]) => mul(a, 1 / (Math.hypot(...a) || 1))
export const mean = (a: number[][]) =>
  mul(
    a.reduce((s, p) => add(s, p), [0, 0, 0]),
    1 / a.length,
  )
export function lookAt(eye: number[], target: number[]) {
  const z = norm(sub(eye, target)),
    x = norm(cross([0, 1, 0], z)),
    y = cross(z, x)
  return [
    x[0],
    y[0],
    z[0],
    0,
    x[1],
    y[1],
    z[1],
    0,
    x[2],
    y[2],
    z[2],
    0,
    -dot(x, eye),
    -dot(y, eye),
    -dot(z, eye),
    1,
  ]
}
export function ortho(l: number, r: number, b: number, t: number, n: number, f: number) {
  return [
    2 / (r - l),
    0,
    0,
    0,
    0,
    2 / (t - b),
    0,
    0,
    0,
    0,
    -2 / (f - n),
    0,
    -(r + l) / (r - l),
    -(t + b) / (t - b),
    -(f + n) / (f - n),
    1,
  ]
}
export function mm(a: ArrayLike<number>, b: ArrayLike<number>) {
  const o = new Float32Array(16)
  for (let c = 0; c < 4; c++)
    for (let r = 0; r < 4; r++) for (let k = 0; k < 4; k++) o[c * 4 + r] += a[k * 4 + r] * b[c * 4 + k]
  return o
}
