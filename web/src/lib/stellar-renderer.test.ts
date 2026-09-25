/// <reference lib="dom" />
import { strict as assert } from 'node:assert'
import { test } from 'node:test'
import { createCanvas, type Canvas } from '@napi-rs/canvas'
import { createStellarRenderer, type StellarDetail } from './stellar-renderer.ts'
import { createStellarMotion } from './stellar-motion.ts'

const makeCanvas = () => createCanvas(1, 1) as unknown as HTMLCanvasElement
const pixels = (canvas: CanvasImageSource) => {
  const c = canvas as unknown as Canvas
  return { data: c.getContext('2d').getImageData(0, 0, c.width, c.height).data, size: c.width }
}
const luminance = (r: number, g: number, b: number) => {
  const f = (c: number) => { c /= 255; return c <= .03928 ? c / 12.92 : ((c + .055) / 1.055) ** 2.4 }
  return .2126 * f(r) + .7152 * f(g) + .0722 * f(b)
}
const contrast = (a: number, b: number) => (Math.max(a, b) + .05) / (Math.min(a, b) + .05)
// Median, so a star inside the patch cannot decide what colour a face is.
function faceLuminance(image: ReturnType<typeof pixels>, fx: number, fy: number) {
  const { data, size } = image, r = Math.round(size * .05), values: number[] = []
  for (let y = Math.round(fy * size) - r; y <= Math.round(fy * size) + r; y++)
    for (let x = Math.round(fx * size) - r; x <= Math.round(fx * size) + r; x++) {
      const o = (y * size + x) * 4
      values.push(luminance(data[o], data[o + 1], data[o + 2]))
    }
  return values.sort((a, b) => a - b)[values.length >> 1]
}
// Face centres of the isometric cube as the renderer frames it.
const TOP = [.5, .26], LEFT = [.29, .62], RIGHT = [.71, .62]
const resting = (overrides: Partial<{ time: number, activity: number, flow: number, drift: number }> = {}) =>
  ({ mode: 'rest' as const, time: 4, activity: 0, flow: 0, drift: .3, ...overrides })

test('the cube is lit from above and all three planes stay separable as the sky travels', () => {
  const renderer = createStellarRenderer(makeCanvas)
  for (const dark of [true, false]) for (const drift of [0, 2, 5, 9]) {
    for (const detail of ['full', 'minimal'] as StellarDetail[]) {
      const image = pixels(renderer.render(resting({ drift }), dark, detail === 'full' ? 288 : 64, detail))
      const [top, left, right] = [TOP, LEFT, RIGHT].map(([x, y]) => faceLuminance(image, x, y))
      const label = `${dark ? 'dark' : 'light'} ${detail} at drift ${drift}`
      assert.ok(top > left && left > right, `${label}: light falls from above (${top}, ${left}, ${right})`)
      assert.ok(contrast(top, left) >= 1.6, `${label}: top separates from left`)
      assert.ok(contrast(left, right) >= 1.5, `${label}: left separates from right`)
    }
  }
  renderer.destroy()
})

test('the cube fills its frame instead of sharing it with a floor shadow', () => {
  const renderer = createStellarRenderer(makeCanvas)
  const { data, size } = pixels(renderer.render(resting(), false, 288))
  let top = size, bottom = 0, left = size, right = 0
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) if (data[(y * size + x) * 4 + 3] > 200) {
    top = Math.min(top, y); bottom = Math.max(bottom, y); left = Math.min(left, x); right = Math.max(right, x)
  }
  assert.ok((bottom - top) / size >= .8, `solid height ${(bottom - top) / size}`)
  assert.ok(Math.abs((left + right) / 2 - size / 2) < size * .02, 'the mark is centred horizontally')
  renderer.destroy()
})

test('the front-corner star survives at favicon size in both themes', () => {
  const renderer = createStellarRenderer(makeCanvas)
  for (const dark of [true, false]) {
    const { data, size } = pixels(renderer.render(resting(), dark, 64, 'minimal'))
    const lum = (x: number, y: number) => { const o = (y * size + x) * 4; return luminance(data[o], data[o + 1], data[o + 2]) }
    const c = size >> 1
    assert.ok(lum(c, c) > .75, 'a bright star marks the front corner')
    // Spikes thick enough to survive the browser shrinking the icon to 16 px: a quarter of the
    // way along each spike, the ray is still at least 2 px wide at 64 px. The corner sits on a
    // pixel boundary, so count across the ray rather than around one pixel.
    for (const [dx, dy] of [[1, 0], [0, -1]]) {
      const x = c + dx * 7, y = c + dy * 7
      const lit = [-2, -1, 0, 1, 2].filter((k) => lum(x + dy * k, y + dx * k) > .35).length
      assert.ok(lit >= 2, `${dark ? 'dark' : 'light'}: spike ${dx ? 'right' : 'up'} is ${lit} px wide`)
    }
  }
  renderer.destroy()
})

test('at rest every star is home, whatever work did to the flow', () => {
  const renderer = createStellarRenderer(makeCanvas)
  const home = Buffer.from(pixels(renderer.render(resting(), true, 288)).data)
  const afterWork = Buffer.from(pixels(renderer.render(resting({ flow: 3.7 }), true, 288)).data)
  assert.ok(afterWork.equals(home))
  const working = Buffer.from(pixels(renderer.render(resting({ flow: 3.7, activity: 1 }), true, 288)).data)
  assert.ok(!working.equals(home), 'activity moves the stars')
  renderer.destroy()
})

test('the sky is identical across renderers, so committed stills match the live icon', () => {
  const a = createStellarRenderer(makeCanvas), b = createStellarRenderer(makeCanvas)
  const motion = createStellarMotion()
  for (let i = 0; i < 180; i++) motion.step(1 / 60, true)
  for (const dark of [true, false])
    assert.ok(Buffer.from(pixels(a.render(motion, dark, 288)).data).equals(Buffer.from(pixels(b.render(motion, dark, 288)).data)))
  a.destroy(); b.destroy()
})

// Some browser Canvas implementations ignore filter assignments entirely.
test('stellar rendering is identical with and without Canvas filter support', () => {
  const plain = createStellarRenderer(makeCanvas)
  const filterless = createStellarRenderer(() => {
    const canvas = createCanvas(1, 1)
    Object.defineProperty(canvas.getContext('2d'), 'filter', { get: () => 'none', set: () => {} })
    return canvas as unknown as HTMLCanvasElement
  })
  for (const dark of [false, true]) {
    const expected = Buffer.from(pixels(plain.render(resting(), dark, 288)).data)
    assert.ok(Buffer.from(pixels(filterless.render(resting(), dark, 288)).data).equals(expected))
  }
  plain.destroy(); filterless.destroy()
})
