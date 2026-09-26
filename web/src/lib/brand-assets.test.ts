import { strict as assert } from 'node:assert'
import { statSync } from 'node:fs'
import { test } from 'node:test'

// These are also the complete download path for reduced-motion sessions.
test('theme and activity stills stay within the immediate icon budget', () => {
  const bytes = (name: string) => statSync(new URL(`../../public/brand/${name}`, import.meta.url)).size
  for (const style of ['', 'stellar/']) for (const theme of ['dark', 'light']) for (const state of ['steady', 'excited']) {
    assert.ok(bytes(`${style}${theme}-${state}.webp`) < 30_000)
    assert.ok(bytes(`${style}favicon-${theme}-${state}.png`) < 15_000)
  }
})


// The tab shows the same drawing as the sidebar, shrunk, as Precession's favicons do.
test('stellar favicons are the main icon at tab size', async () => {
  const { createCanvas, loadImage } = await import('@napi-rs/canvas')
  const coverage = async (name: string) => {
    const image = await loadImage(new URL(`../../public/brand/stellar/${name}`, import.meta.url).pathname)
    const canvas = createCanvas(64, 64)
    const ctx = canvas.getContext('2d')
    ctx.drawImage(image, 0, 0, 64, 64)
    const data = ctx.getImageData(0, 0, 64, 64).data
    let lit = 0
    for (let i = 3; i < data.length; i += 4) lit += data[i] / 255
    return lit / (64 * 64)
  }
  for (const theme of ['dark', 'light']) for (const state of ['steady', 'excited']) {
    const still = await coverage(`${theme}-${state}.webp`)
    const favicon = await coverage(`favicon-${theme}-${state}.png`)
    assert.ok(Math.abs(still - favicon) < 0.03, `${theme}-${state}: favicon covers ${favicon}, still ${still}`)
  }
})
