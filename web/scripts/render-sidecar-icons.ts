// Run from web: node --experimental-strip-types scripts/render-sidecar-icons.ts
// Native icon families are high-resolution stills from the main application's Precession renderer.
import { createCanvas, loadImage } from '@napi-rs/canvas'
import { chromium } from '@playwright/test'
import { mkdir, writeFile } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'
import { createServer } from 'vite'

const mac = new URL('../../sidecars/macos/', import.meta.url)
const windows = new URL('../../sidecars/windows/src/Optimisarr.Sidecar.Tray/Resources/', import.meta.url)
await mkdir(new URL('Sources/OptimisarrSidecar/Resources/', mac), { recursive: true })
await mkdir(windows, { recursive: true })

const server = await createServer({
  root: fileURLToPath(new URL('../', import.meta.url)),
  server: { host: '127.0.0.1', port: 0 },
})
await server.listen()
const browser = await chromium.launch({ headless: true })
try {
  const page = await browser.newPage()
  await page.goto(server.resolvedUrls.local[0] + 'scripts/brand/preview.html')
  const rendered = await page.evaluate(async () => {
    const { createBrandRenderer } = await import('/src/lib/brand-renderer.ts')
    const { createBrandMotion } = await import('/src/lib/brand-motion.ts')
    const renderer = createBrandRenderer()
    const output: Record<string, string> = {}
    try {
      for (const dark of [true, false]) {
        const source = renderer.render(createBrandMotion(), dark, 2048)
        const icon = document.createElement('canvas')
        icon.width = icon.height = 1024
        const context = icon.getContext('2d')!
        context.imageSmoothingEnabled = true
        context.imageSmoothingQuality = 'high'
        context.drawImage(source, 0, 0, 1024, 1024)
        output[dark ? 'BrandMark' : 'BrandMarkLight'] = icon.toDataURL('image/png')
      }
    } finally {
      renderer.destroy()
    }
    return output
  })

  for (const [name, dataURL] of Object.entries(rendered)) {
    const png = Buffer.from(dataURL.split(',')[1], 'base64')
    await writeFile(new URL(`Sources/OptimisarrSidecar/Resources/${name}.png`, mac), png)
    await writeFile(new URL(`${name}.png`, windows), png)
    if (name !== 'BrandMark') continue

    await writeFile(new URL('Resources/AppIcon.png', mac), png)
    const image = await loadImage(png)
    const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    const frames = sizes.map((size) => {
      const frame = createCanvas(size, size)
      const context = frame.getContext('2d')
      context.imageSmoothingEnabled = true
      context.imageSmoothingQuality = 'high'
      context.drawImage(image, 0, 0, size, size)
      return frame.toBuffer('image/png')
    })
    const header = Buffer.alloc(6 + 16 * frames.length)
    header.writeUInt16LE(1, 2)
    header.writeUInt16LE(frames.length, 4)
    let offset = header.length
    frames.forEach((frame, index) => {
      const at = 6 + index * 16
      header[at] = header[at + 1] = sizes[index] === 256 ? 0 : sizes[index]
      header.writeUInt16LE(1, at + 4)
      header.writeUInt16LE(32, at + 6)
      header.writeUInt32LE(frame.length, at + 8)
      header.writeUInt32LE(offset, at + 12)
      offset += frame.length
    })
    await writeFile(new URL('AppIcon.ico', windows), Buffer.concat([header, ...frames]))
  }
} finally {
  await browser.close()
  await server.close()
}
