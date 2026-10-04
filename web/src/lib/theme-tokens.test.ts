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

// The accent and every state colour are tokens too. A component that names a palette hue
// (`bg-cyan-500`, `accent-cyan-600`) keeps that hue whatever the theme says, so a retheme
// stops at it and leaves one stray colour behind. Media overlays are exempt for the same
// reason as above.
const paletteHue = /\b(?:[a-z-]+:)*(?:bg|text|border|ring|outline|accent|from|via|to|fill|stroke|decoration)-(?:cyan|sky|emerald|green|teal|lime|red|rose|amber|yellow|orange|blue|indigo|violet|purple|pink)-\d{2,3}\b/g

test('components take the accent and state colours from tokens, never from a palette hue', () => {
  const offenders: string[] = []
  for (const file of sources(root)) {
    const name = relative(root, file)
    if (overlays.has(name)) continue
    for (const match of readFileSync(file, 'utf8').matchAll(paletteHue)) offenders.push(`${name}: ${match[0]}`)
  }
  const css = readFileSync(join(root, 'app.css'), 'utf8')
  for (const match of css.matchAll(/--color-(?:cyan|sky|emerald|red|amber)-\d{2,3}|\b(?:bg|accent)-(?:cyan|sky|emerald)-\d{2,3}\b/g)) {
    offenders.push(`app.css: ${match[0]}`)
  }
  assert.deepEqual(offenders, [], `palette hues found:\n${offenders.join('\n')}`)
})

// State is read from colour by everyone, including the one reader in twelve with a red-green
// deficiency. Every state also carries a glyph and a word, but the colours must not lie to
// that reader either: ok, warn and bad stay apart under simulated deuteranopia and protanopia
// (OKLab ΔE ×100 ≥ 8, Machado 2009 at full severity), apart for full colour vision (≥ 15), and
// legible as text on a panel (WCAG 4.5:1).
type Rgb = [number, number, number]

function hex(value: string): Rgb {
  const h = value.replace('#', '')
  return [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16) / 255) as Rgb
}
const toLinear = (c: number) => (c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4)
const linear = (rgb: Rgb) => rgb.map(toLinear) as Rgb

const machado: Record<string, number[][]> = {
  deutan: [[0.367322, 0.860646, -0.227968], [0.280085, 0.672501, 0.047413], [-0.01182, 0.04294, 0.968881]],
  protan: [[0.152286, 1.052583, -0.204868], [0.114503, 0.786281, 0.099216], [-0.003882, -0.048116, 1.051998]],
}
function simulate(lin: Rgb, kind: string): Rgb {
  return machado[kind].map((row) => Math.min(1, Math.max(0, row[0] * lin[0] + row[1] * lin[1] + row[2] * lin[2]))) as Rgb
}
function oklab([r, g, b]: Rgb): Rgb {
  const l = Math.cbrt(0.4122214708 * r + 0.5363710205 * g + 0.0514459929 * b)
  const m = Math.cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b)
  const s = Math.cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b)
  return [
    0.2104542553 * l + 0.793617785 * m - 0.0040720468 * s,
    1.9779984951 * l - 2.428592205 * m + 0.4505937099 * s,
    0.0259040371 * l + 0.7827717662 * m - 0.808675766 * s,
  ]
}
const deltaE = (a: Rgb, b: Rgb) => 100 * Math.hypot(a[0] - b[0], a[1] - b[1], a[2] - b[2])
const luminance = (lin: Rgb) => 0.2126 * lin[0] + 0.7152 * lin[1] + 0.0722 * lin[2]
function contrast(a: string, b: string): number {
  const [x, y] = [luminance(linear(hex(a))), luminance(linear(hex(b)))].sort((p, q) => q - p)
  return (x + 0.05) / (y + 0.05)
}

function scheme(css: string, selector: RegExp): Record<string, string> {
  const start = css.search(selector)
  assert.ok(start >= 0, `no ${selector} block in app.css`)
  const block = css.slice(start, css.indexOf('\n}', start))
  return Object.fromEntries([...block.matchAll(/--([a-z0-9-]+):\s*(#[0-9a-f]{6})\s*;/gi)].map((m) => [m[1], m[2]]))
}

test('state colours stay distinguishable for colour-blind readers and legible on a panel', () => {
  const css = readFileSync(join(root, 'app.css'), 'utf8')
  const problems: string[] = []
  for (const [name, selector] of [['light', /^:root\s*\{/m], ['dark', /^html\.dark\b/m]] as const) {
    const tokens = scheme(css, selector)
    const states = ['ok', 'warn', 'bad'] as const
    for (const state of [...states, 'accent', 'ink-3']) {
      assert.ok(tokens[state], `${name}: --${state} must be a six-digit hex`)
      const ratio = contrast(tokens[state], tokens.panel)
      if (ratio < 4.5) problems.push(`${name}: --${state} on --panel is ${ratio.toFixed(2)}:1`)
    }
    for (let i = 0; i < states.length; i++) {
      for (let j = i + 1; j < states.length; j++) {
        const [a, b] = [linear(hex(tokens[states[i]])), linear(hex(tokens[states[j]]))]
        const normal = deltaE(oklab(a), oklab(b))
        if (normal < 15) problems.push(`${name}: ${states[i]}/${states[j]} ΔE ${normal.toFixed(1)} for full colour vision`)
        for (const kind of Object.keys(machado)) {
          const seen = deltaE(oklab(simulate(a, kind)), oklab(simulate(b, kind)))
          if (seen < 8) problems.push(`${name}: ${states[i]}/${states[j]} ΔE ${seen.toFixed(1)} under ${kind}`)
        }
      }
    }
  }
  assert.deepEqual(problems, [])
})
