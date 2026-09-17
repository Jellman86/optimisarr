// Run from web: node --experimental-strip-types scripts/render-sidecar-icons.ts
// Native icon families share the main application's Stellar renderer and textures.
import { createCanvas, loadImage, type Canvas } from '@napi-rs/canvas'
import { mkdir, writeFile } from 'node:fs/promises'
import { createStellarRenderer } from '../src/lib/stellar-renderer.ts'
import { createStellarMotion } from '../src/lib/stellar-motion.ts'
const mac = new URL('../../sidecars/macos/', import.meta.url)
const windows = new URL('../../sidecars/windows/src/Optimisarr.Sidecar.Tray/Resources/', import.meta.url)
await mkdir(new URL('Sources/OptimisarrSidecar/Resources/', mac), { recursive: true })
await mkdir(windows, { recursive: true })
const scenes = await Promise.all(['blue', 'pink', 'gold'].map(name => loadImage(new URL(`../src/lib/assets/stellar/${name}-scene.jpg`, import.meta.url).pathname)))
const renderer = createStellarRenderer(scenes as unknown as CanvasImageSource[], () => createCanvas(1, 1) as unknown as HTMLCanvasElement)
for (const dark of [true, false]) {
 const full = renderer.render(createStellarMotion(), dark, 1536) as unknown as Canvas
 const icon = createCanvas(1024, 1024), scale = 1536 / 288
 icon.getContext('2d').drawImage(full, 45 * scale, 14 * scale, 194 * scale, 194 * scale, 0, 0, 1024, 1024)
 const name = dark ? 'BrandMark' : 'BrandMarkLight'
 await writeFile(new URL(`Sources/OptimisarrSidecar/Resources/${name}.png`, mac), icon.toBuffer('image/png'))
 await writeFile(new URL(`${name}.png`, windows), icon.toBuffer('image/png'))
 if (!dark) continue
 await writeFile(new URL('Resources/AppIcon.png', mac), icon.toBuffer('image/png'))
 const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
 const frames = sizes.map(size => { const frame = createCanvas(size, size); frame.getContext('2d').drawImage(icon, 0, 0, size, size); return frame.toBuffer('image/png') })
 const header = Buffer.alloc(6 + 16 * frames.length)
 header.writeUInt16LE(1, 2); header.writeUInt16LE(frames.length, 4)
 let offset = header.length
 frames.forEach((frame, index) => {
  const at = 6 + index * 16
  header[at] = header[at + 1] = sizes[index] === 256 ? 0 : sizes[index]
  header.writeUInt16LE(1, at + 4); header.writeUInt16LE(32, at + 6)
  header.writeUInt32LE(frame.length, at + 8); header.writeUInt32LE(offset, at + 12)
  offset += frame.length
 })
 await writeFile(new URL('AppIcon.ico', windows), Buffer.concat([header, ...frames]))
}
renderer.destroy()
