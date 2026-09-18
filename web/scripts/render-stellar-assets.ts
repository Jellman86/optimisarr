// Run `npm run brand:assets` after changing the stellar renderer or textures.
import { createCanvas, loadImage, type Canvas } from '@napi-rs/canvas'
import { writeFile, mkdir } from 'node:fs/promises'
import { createStellarRenderer } from '../src/lib/stellar-renderer.ts'
import { createStellarMotion } from '../src/lib/stellar-motion.ts'

const destination = new URL('../public/brand/stellar/', import.meta.url)
await mkdir(destination, { recursive: true })
const scenes = await Promise.all(
  ['blue', 'pink', 'gold'].map((name) =>
    loadImage(new URL(`../src/lib/assets/stellar/${name}-scene.jpg`, import.meta.url).pathname),
  ),
)
const renderer = createStellarRenderer(
  scenes as unknown as CanvasImageSource[],
  () => createCanvas(1, 1) as unknown as HTMLCanvasElement,
)
for (const dark of [true, false])
  for (const working of [false, true]) {
    const motion = createStellarMotion()
    if (working) for (let i = 0; i < 17 * 60; i++) motion.step(1 / 60, true)
    const image = renderer.render(motion, dark, 288) as unknown as Canvas
    const suffix = `${dark ? 'dark' : 'light'}-${working ? 'excited' : 'steady'}`
    await writeFile(new URL(`${suffix}.webp`, destination), image.toBuffer('image/webp', 88))
    // Tight crop keeps the three faces readable in a browser tab; the sidebar retains its shadow.
    const favicon = createCanvas(64, 64)
    favicon.getContext('2d').drawImage(image, 45, 14, 194, 194, 0, 0, 64, 64)
    await writeFile(new URL(`favicon-${suffix}.png`, destination), favicon.toBuffer('image/png'))
  }
renderer.destroy()
