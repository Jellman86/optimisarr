// Snapshot the same geometry, materials and camera used by the application.
import { chromium } from '@playwright/test'
import { createServer } from 'vite'
import { writeFile, mkdir } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'
const output = fileURLToPath(new URL('../../public/brand/', import.meta.url))
await mkdir(output, { recursive: true })
const server = await createServer({ root: fileURLToPath(new URL('../../', import.meta.url)), server: { port: 0, host: '127.0.0.1' } })
await server.listen()
const browser = await chromium.launch({ headless: true })
try {
  const page = await browser.newPage()
  await page.goto(server.resolvedUrls.local[0] + 'scripts/brand/preview.html')
  const snapshots = await page.evaluate(async () => {
    const { createBrandRenderer } = await import('/src/lib/brand-renderer.ts')
    const { createBrandMotion } = await import('/src/lib/brand-motion.ts')
    const renderer = createBrandRenderer()
    const output = {}
    try {
      for (const dark of [true, false]) for (const working of [false, true]) {
        const motion = createBrandMotion()
        if (working) for (let i = 0; i < 390; i++) motion.step(1 / 120, true)
        const source = renderer.render(motion, dark, 576)
        const theme = dark ? 'dark' : 'light', state = working ? 'excited' : 'steady'
        const exports = [[`${theme}-${state}.webp`, 288], [`favicon-${theme}-${state}.png`, 64]]
        if (dark && !working) exports.push(['../favicon.png', 32], ['../favicon-192.png', 192], ['../apple-touch-icon.png', 180])
        for (const [name, size] of exports) {
          const canvas = document.createElement('canvas')
          canvas.width = canvas.height = size
          const ctx = canvas.getContext('2d')
          ctx.imageSmoothingQuality = 'high'
          ctx.drawImage(source, 0, 0, size, size)
          output[name] = canvas.toDataURL(name.endsWith('.webp') ? 'image/webp' : 'image/png', .9)
        }
      }
    } finally { renderer.destroy() }
    return output
  })
  for (const [name, url] of Object.entries(snapshots)) {
    const bytes = Buffer.from(url.split(',')[1], 'base64')
    await writeFile(`${output}/${name}`, bytes)
    console.log(`${name}: ${bytes.length} bytes`)
  }
} finally {
  await browser.close()
  await server.close()
}
