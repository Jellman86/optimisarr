// Run `npm run brand:assets` after changing the stellar renderer, sky or motion.
// The committed stills are what reduced-motion sessions and browser tabs show, so they are
// rendered by the same seeded renderer as the live icon.
import { createCanvas, type Canvas } from '@napi-rs/canvas'
import { writeFile, mkdir } from 'node:fs/promises'
import { createStellarRenderer } from '../src/lib/stellar-renderer.ts'
import { createStellarMotion } from '../src/lib/stellar-motion.ts'

const destination = new URL('../public/brand/stellar/', import.meta.url)
await mkdir(destination, { recursive: true })
const renderer = createStellarRenderer(() => createCanvas(1, 1) as unknown as HTMLCanvasElement)
for (const dark of [true, false])
  for (const working of [false, true]) {
    const motion = createStellarMotion()
    // Mid-stream, with the front star at the peak of its pulse.
    if (working) for (let i = 0; i < Math.round(3.436 * 120); i++) motion.step(1 / 120, true)
    const suffix = `${dark ? 'dark' : 'light'}-${working ? 'excited' : 'steady'}`
    const still = renderer.render(motion, dark, 288) as unknown as Canvas
    await writeFile(new URL(`${suffix}.webp`, destination), still.toBuffer('image/webp', 88))
    // Tabs get the small-size drawing (planes, edges, front star), not a shrunken photograph.
    const favicon = renderer.render(motion, dark, 64, 'minimal') as unknown as Canvas
    await writeFile(new URL(`favicon-${suffix}.png`, destination), favicon.toBuffer('image/png'))
  }
renderer.destroy()
