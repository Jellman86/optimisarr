import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join, relative } from 'node:path'

// The theme lives in one place: app.css defines an ink scale, a surface set and five tones,
// and both colour schemes swap as a set. A component that spells out its own light and dark
// colours (`text-slate-500 dark:text-slate-400`) has opted out of that — it will not follow
// a palette change, and it is one more pair to keep in step by hand. This test keeps the
// opt-outs at zero.
//
// Media overlays are the one legitimate exception: a caption over a video frame is drawn on
// the picture, not on a surface, so it is dark in both themes on purpose.

const root = join(import.meta.dirname, '..')

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name)
    return statSync(path).isDirectory() ? sources(path) : path.endsWith('.svelte') ? [path] : []
  })
}

const handPaired = /\bdark:(hover:|focus:|group-hover:)?(text|bg|border|ring|via|from|to|divide)-(slate|gray|zinc|white|black|cyan|sky|red|amber|emerald|green|violet)\b/g
const lightOnly = /(^|[\s"'`{])(hover:|focus:)?(text|bg)-(slate|gray|zinc)-\d{2,3}\b/g

const overlays = new Set(['lib/components/MediaCompare.svelte'])

test('components take their colours from the theme tokens, not from per-scheme pairs', () => {
  const offenders: string[] = []
  for (const file of sources(root)) {
    const name = relative(root, file)
    if (overlays.has(name)) continue
    const text = readFileSync(file, 'utf8')
    for (const match of text.matchAll(handPaired)) offenders.push(`${name}: ${match[0]}`)
    for (const match of text.matchAll(lightOnly)) offenders.push(`${name}: ${match[0].trim()}`)
  }
  assert.deepEqual(offenders, [], `hand-paired colours found:\n${offenders.join('\n')}`)
})
