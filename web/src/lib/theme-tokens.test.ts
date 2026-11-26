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
    for (const fill of ['accent-fill', 'accent-fill-hi'] as const) {
      assert.ok(tokens[fill] && tokens['accent-on'], `${name}: --${fill} and --accent-on must be six-digit hex`)
      const ratio = contrast(tokens['accent-on'], tokens[fill])
      if (ratio < 4.5) problems.push(`${name}: --accent-on on --${fill} is ${ratio.toFixed(2)}:1`)
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

// A control that is not text still has to be seen to be used: a field's edge, the track of a
// switch that is off and the focus ring each clear WCAG 1.4.11's 3:1 against the surfaces they
// are drawn on. axe does not measure these, so this does. A translucent token is measured as it
// is painted: laid over the surface it sits on.
type Rgba = [number, number, number, number]

function rgba(value: string): Rgba {
  const hexMatch = /^#([0-9a-f]{6})$/i.exec(value)
  if (hexMatch) return [...hex(value).map((c) => c * 255), 1] as Rgba
  const fn = /^rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*(?:,\s*([\d.]+)\s*)?\)$/i.exec(value)
  assert.ok(fn, `cannot read the colour ${value}`)
  return [Number(fn[1]), Number(fn[2]), Number(fn[3]), fn[4] === undefined ? 1 : Number(fn[4])]
}
function over(top: string, surface: string): string {
  const [r, g, b, a] = rgba(top)
  const base = rgba(surface)
  assert.equal(base[3], 1, `${surface} must be an opaque surface`)
  const mix = [r, g, b].map((c, i) => Math.round(c * a + base[i] * (1 - a)))
  return `#${mix.map((c) => c.toString(16).padStart(2, '0')).join('')}`
}
function colours(css: string, selector: RegExp): Record<string, string> {
  const start = css.search(selector)
  assert.ok(start >= 0, `no ${selector} block in app.css`)
  const block = css.slice(start, css.indexOf('\n}', start))
  return Object.fromEntries([...block.matchAll(/--([a-z0-9-]+):\s*(#[0-9a-f]{6}|rgba?\([^)]*\))\s*;/gi)].map((m) => [m[1], m[2]]))
}

test('controls that are not text clear 3:1 on the surfaces they sit on, in both schemes', () => {
  const css = readFileSync(join(root, 'app.css'), 'utf8')
  const directFloors: [token: string, surfaces: string[]][] = [
    ['switch-off', ['panel']],
    ['focus', ['panel', 'ground']],
  ]
  const problems: string[] = []
  for (const [name, selector] of [['light', /^:root\s*\{/m], ['dark', /^html\.dark\b/m]] as const) {
    const tokens = colours(css, selector)
    for (const [token, surfaces] of directFloors) {
      assert.ok(tokens[token], `${name}: --${token} must be defined`)
      for (const surface of surfaces) {
        const ratio = contrast(over(tokens[token], tokens[surface]), tokens[surface])
        if (ratio < 3) problems.push(`${name}: --${token} on --${surface} is ${ratio.toFixed(2)}:1`)
      }
    }
    // An input edge is an inset shadow painted over --field, viewed adjacent to the surrounding
    // surface (--panel or --ground). Model both painted layers rather than compositing directly
    // onto the surrounding surface.
    assert.ok(tokens.field, `${name}: --field must be defined`)
    for (const token of ['field-edge', 'field-edge-hover'] as const) {
      assert.ok(tokens[token], `${name}: --${token} must be defined`)
      for (const surface of ['panel', 'ground'] as const) {
        const fieldFill = over(tokens.field, tokens[surface])
        const paintedEdge = over(tokens[token], fieldFill)
        const ratio = contrast(paintedEdge, tokens[surface])
        if (ratio < 3) problems.push(`${name}: --${token} (over --field) against --${surface} is ${ratio.toFixed(2)}:1`)
      }
    }
    // Pointing at a field strengthens its edge; it never fades it.
    const panelField = over(tokens.field, tokens.panel)
    const resting = contrast(over(tokens['field-edge'], panelField), tokens.panel)
    const hovered = contrast(over(tokens['field-edge-hover'], panelField), tokens.panel)
    if (hovered <= resting) problems.push(`${name}: a hovered field edge (${hovered.toFixed(2)}:1) is no stronger than at rest (${resting.toFixed(2)}:1)`)
    // The white knob still stands out from an "off" track.
    const knob = contrast('#ffffff', tokens['switch-off'])
    if (knob < 3) problems.push(`${name}: the switch knob on --switch-off is ${knob.toFixed(2)}:1`)
  }
  assert.deepEqual(problems, [])
})

// Drift budgets. Each figure is what the components carried when its budget was set: one-off
// type sizes, stroked containers, per-button height overrides and page-scoped CSS are the ways
// pages drift from the shared system. A budget only ever comes down. When a change removes
// some drift, lower the budget to the new count in the same change so it cannot creep back.
function countArbitraryFontSizes(text: string): number {
  return text.match(/(?<![\w-])text-\[(?:length:)?\d*\.?\d+(?:px|rem)\]/g)?.length ?? 0
}
function countStrokedEdges(text: string): number {
  return text.match(/(?<![\w-])border-line(?![\w-])/g)?.length ?? 0
}
function countMinH11Overrides(text: string): number {
  return text.match(/(?<![\w-])min-h-11(?![\w.-])/g)?.length ?? 0
}
// A duration spelled out by hand (`duration-200`, `transition: opacity 180ms`, `{ duration: 180 }`)
// keeps its speed when the motion tokens change and keeps moving when reduced motion is asked
// for. Keyframe loop periods (`animation: … 1.8s infinite`) are not counted: they are the shape
// of one indicator, and each already stops under reduced motion.
const cssTime = /(?<![\w.-])(?:\d+\.?\d*|\.\d+)m?s(?![\w-])/g
function countRawDurations(text: string): number {
  let count = text.match(/(?<![\w-])(?:duration|delay)-(?:\d+|\[)/g)?.length ?? 0
  for (const match of text.matchAll(/(?<![\w-])transition(?:-duration|-delay)?\s*:([^;}]*)/g)) {
    count += match[1].match(cssTime)?.filter((time) => parseFloat(time) !== 0).length ?? 0
  }
  for (const match of text.matchAll(/(?<![\w-])(?:duration|delay):\s*([^,}]*)/g)) {
    count += match[1].match(/(?<![\w.])[1-9]\d*(?![\w.])/g)?.length ?? 0
  }
  return count
}
function countStyleLines(text: string): number {
  let lines = 0
  for (const match of text.matchAll(/<style[^>]*>([\s\S]*?)<\/style>/g)) {
    const body = match[1].replace(/^\r?\n/, '').replace(/\r?\n$/, '')
    if (body.length > 0) lines += body.split(/\r?\n/).length
  }
  return lines
}

test('drift counters handle typed lengths, fractional heights and one-line style blocks', () => {
  assert.equal(countArbitraryFontSizes('text-[11px] text-[length:12px] text-[0.75rem] text-[length:0.8rem] text-sm'), 4)
  assert.equal(countStrokedEdges('border-line border-line-soft my-border-line'), 1)
  assert.equal(countMinH11Overrides('min-h-11 min-h-11.5 min-h-110 max-min-h-11 sm:min-h-11'), 2)
  assert.equal(countStyleLines('<style></style><style>\n</style>'), 0)
  assert.equal(countStyleLines('<style>.a { color: red; }</style>'), 1)
  assert.equal(countStyleLines('<style>\n  .a {}\n  .b {}\n</style>'), 2)
  assert.equal(countRawDurations('transition-colors duration-200 duration-(--motion-base) md:duration-150'), 2)
  assert.equal(countRawDurations('a { transition: opacity 180ms ease, visibility 0s linear .4s; }'), 2)
  assert.equal(countRawDurations('a { transition: opacity var(--motion-base) var(--motion-ease-standard); }'), 0)
  assert.equal(countRawDurations('animate:flip={{ duration: reduced ? 0 : 180 }} {{ duration: motionMs(\'base\') }}'), 1)
  assert.equal(countRawDurations('a { animation: pulse 1.8s ease-in-out infinite; } { durationSeconds: 12 }'), 0)
})

// Every transition takes its speed from the motion tokens, so the whole app speeds up, slows
// down or stands still together. app.css is included: it is where the tokens are defined, and
// its shared primitives are held to them as well.
test('transitions take their durations from the motion tokens', () => {
  const files = [...sources(root), join(root, 'app.css')]
  const offenders = files
    .map((file) => ({ name: relative(root, file), n: countRawDurations(readFileSync(file, 'utf8')) }))
    .filter(({ n }) => n > 0)
    .map(({ name, n }) => `${name}: ${n}`)
  assert.deepEqual(offenders, [], `raw durations found; use var(--motion-fast|base|slow) or motionMs():\n${offenders.join('\n')}`)
})

const budgets = {
  'arbitrary font sizes (text-[…px|rem])': { budget: 0, count: countArbitraryFontSizes },
  'stroked edges (border-line)': { budget: 0, count: countStrokedEdges },
  'per-control height overrides (min-h-11)': { budget: 0, count: countMinH11Overrides },
  'lines of page-scoped CSS (<style>)': { budget: 573, count: countStyleLines },
}

for (const [drift, { budget, count }] of Object.entries(budgets)) {
  test(`components stay within the drift budget for ${drift}`, () => {
    const perFile = sources(root)
      .map((file) => ({ name: relative(root, file), n: count(readFileSync(file, 'utf8')) }))
      .filter(({ n }) => n > 0)
      .sort((a, b) => b.n - a.n)
    const total = perFile.reduce((sum, { n }) => sum + n, 0)
    assert.ok(
      total <= budget,
      `${drift}: ${total} found, budget ${budget}. Use the shared primitives instead. Largest contributors:\n` +
        perFile.slice(0, 10).map(({ name, n }) => `  ${name}: ${n}`).join('\n'),
    )
    assert.ok(
      total >= budget,
      `${drift}: ${total} found, budget ${budget}. Drift went down — lower the budget to ${total} so it stays down.`,
    )
  })
}
