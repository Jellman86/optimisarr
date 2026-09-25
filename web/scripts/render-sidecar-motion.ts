// Run from web: node --experimental-strip-types scripts/render-sidecar-motion.ts
// Native tray frames are snapshots of the application's actual geometry and shaders.
import { chromium } from '@playwright/test'
import { mkdir, writeFile } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'
import { createServer } from 'vite'

const roots = [
  new URL('../../sidecars/macos/Sources/OptimisarrSidecar/Resources/', import.meta.url),
  new URL('../../sidecars/windows/src/Optimisarr.Sidecar.Tray/Resources/', import.meta.url),
]
for (const root of roots) await mkdir(root, { recursive: true })
const server = await createServer({
  root: fileURLToPath(new URL('../', import.meta.url)),
  server: { host: '127.0.0.1', port: 0 },
})
await server.listen()
const browser = await chromium.launch({ headless: true })
try {
  const page = await browser.newPage()
  await page.goto(server.resolvedUrls.local[0] + 'scripts/brand/preview.html')
  for (const dark of [true, false]) {
    const dataURL = await page.evaluate(async (dark) => {
      const { createBrandRenderer } = await import('/src/lib/brand-renderer.ts')
      const { createBrandMotion } = await import('/src/lib/brand-motion.ts')
      const renderer = createBrandRenderer()
      const motion = createBrandMotion()
      const frames = 88
      const cell = 48
      const columns = 11
      const sheet = document.createElement('canvas')
      sheet.width = columns * cell
      sheet.height = 8 * cell
      const context = sheet.getContext('2d')!
      context.imageSmoothingEnabled = true
      context.imageSmoothingQuality = 'high'
      // Keep the light's resting azimuth. Its shadow still responds to every moving slice,
      // while the atlas closes on the same illumination as the packaged idle mark.
      const restingLight = motion.lightPhase
      try {
        for (let frame = 0; frame < frames; frame++) {
          if (frame > 0) for (let tick = 0; tick < 12; tick++) motion.step(1 / 120, true)
          const source = renderer.render({ ...motion, lightPhase: restingLight }, dark, cell * 4)
          context.drawImage(source, frame % columns * cell, Math.floor(frame / columns) * cell, cell, cell)
        }
      } finally { renderer.destroy() }
      return sheet.toDataURL('image/png')
    }, dark)
    const name = dark ? 'BrandMotion.png' : 'BrandMotionLight.png'
    const data = Buffer.from(dataURL.split(',')[1], 'base64')
    for (const root of roots) await writeFile(new URL(name, root), data)
    console.log(`${name}: ${data.length} bytes`)
  }
} finally {
  await browser.close()
  await server.close()
}
