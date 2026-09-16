// Offline only: no shader, WebGL context or ray marching ships in the application bundle.
import { createRequire } from 'node:module'
import { readFile, writeFile, mkdir } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'
const { chromium } = createRequire(import.meta.url)('@playwright/test')
const output = fileURLToPath(new URL('../../public/brand/', import.meta.url))
await mkdir(output, { recursive: true })
const browser = await chromium.launch({ headless: true })
try {
  const page = await browser.newPage()
  page.on('pageerror', error => { throw error })
  await page.setContent(await readFile(new URL('./scene.html', import.meta.url), 'utf8'))
  await page.evaluate(() => {
    function surface(size, columns = 1, rows = 1) {
      const canvas = document.createElement('canvas')
      canvas.width = size * columns
      canvas.height = size * rows
      const ctx = canvas.getContext('2d')
      ctx.imageSmoothingEnabled = true
      ctx.imageSmoothingQuality = 'high'
      return { canvas, ctx, size }
    }
    window.output = {
      'active.webp': surface(288, 8, 6),
      'active-small.webp': surface(80, 8, 6),
      'steady.webp': surface(288),
      'excited.webp': surface(288),
      'favicon-steady.png': surface(64),
      'favicon-excited.png': surface(64),
    }
    window.draw = (phase, energy, names, frame = 0) => {
      // Four samples per delivered texel, then another downsample to CSS pixels.
      const source = window.brandScene.render(phase, energy, 576)
      for (const name of names) {
        const { ctx, size } = window.output[name]
        const inset = size <= 80 ? 60 : 0
        ctx.drawImage(source, inset, inset, source.width - inset * 2, source.height - inset * 2,
          (frame % 8) * size, Math.floor(frame / 8) * size, size, size)
      }
    }
  })
  await page.evaluate(() => window.draw(0, 0, ['steady.webp', 'favicon-steady.png']))
  await page.evaluate(() => window.draw(0, 1, ['excited.webp', 'favicon-excited.png']))
  for (let frame = 0; frame < 48; frame++) {
    await page.evaluate(frame => window.draw(frame * Math.PI * 2 / 48, 1, ['active.webp', 'active-small.webp'], frame), frame)
    if (frame % 8 === 0) console.log(`Rendered ${frame + 1}/48 frames`)
  }
  for (const name of ['active.webp', 'active-small.webp', 'steady.webp', 'excited.webp', 'favicon-steady.png', 'favicon-excited.png']) {
    const url = await page.evaluate(name => window.output[name].canvas.toDataURL(name.endsWith('.png') ? 'image/png' : 'image/webp', .84), name)
    const bytes = Buffer.from(url.split(',')[1], 'base64')
    await writeFile(`${output}/${name}`, bytes)
    console.log(`${name}: ${bytes.length} bytes`)
  }
  for (const [name, size] of [['favicon.png', 32], ['favicon-192.png', 192], ['apple-touch-icon.png', 180]]) {
    const url = await page.evaluate(size => {
      const canvas = document.createElement('canvas')
      canvas.width = canvas.height = size
      const ctx = canvas.getContext('2d')
      ctx.imageSmoothingQuality = 'high'
      ctx.drawImage(window.output['excited.webp'].canvas, 30, 30, 228, 228, 0, 0, size, size)
      return canvas.toDataURL()
    }, size)
    await writeFile(`${output}/../${name}`, Buffer.from(url.split(',')[1], 'base64'))
  }
} finally {
  await browser.close()
}
