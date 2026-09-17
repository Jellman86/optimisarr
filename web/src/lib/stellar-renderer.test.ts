/// <reference lib="dom" />
import { strict as assert } from 'node:assert'
import { test } from 'node:test'
import { createCanvas, loadImage, type Canvas } from '@napi-rs/canvas'
import { createStellarRenderer } from './stellar-renderer.ts'
import { createStellarMotion } from './stellar-motion.ts'

// Some browser Canvas implementations ignore filter assignments entirely.
// The actual material, halo and cast shadow must survive that capability gap.
test('stellar lighting is identical with and without Canvas filter support', async () => {
  const scenes = await Promise.all(['blue', 'pink', 'gold'].map(name =>
    loadImage(new URL(`./assets/stellar/${name}-scene.jpg`, import.meta.url).pathname)))
  const render = (filters: boolean) => createStellarRenderer(scenes as unknown as CanvasImageSource[], () => {
    const canvas = createCanvas(1, 1)
    if (!filters) Object.defineProperty(canvas.getContext('2d'), 'filter', { get: () => 'none', set: () => {} })
    return canvas as unknown as HTMLCanvasElement
  })
  const supported = render(true), unsupported = render(false)
  const motion = createStellarMotion()
  for (const working of [false, true]) {
    if (working) for (let i = 0; i < 17 * 60; i++) motion.step(1 / 60, true)
    for (const dark of [false, true]) {
      const a = supported.render(motion, dark, 288) as unknown as Canvas
      const b = unsupported.render(motion, dark, 288) as unknown as Canvas
      const expected = Buffer.from(a.getContext('2d').getImageData(0, 0, 288, 288).data)
      const actual = Buffer.from(b.getContext('2d').getImageData(0, 0, 288, 288).data)
      assert.ok(actual.equals(expected), `Filter support must not change the ${dark ? 'dark' : 'light'} ${working ? 'working' : 'idle'} icon`)
    }
  }
  const still = supported.render(createStellarMotion(), false, 288) as unknown as Canvas
  const shadow = still.getContext('2d').getImageData(80, 213, 140, 55).data
  const opacity = Array.from(shadow).filter((_, index) => index % 4 === 3)
  assert.ok(Math.max(...opacity) > 10, 'A visible cast shadow must survive without Canvas filters')
  assert.ok(Math.max(...opacity) < 65, 'The shadow should be translucent, not a solid platform')
  supported.destroy(); unsupported.destroy()
})
