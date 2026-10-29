import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import vm from 'node:vm'
import { parseThemeMode, resolveDark, THEME_STORAGE_KEY } from './theme.ts'

// The page must be in the right scheme before it is painted, so each entry point carries a
// small boot script that settles it from the stored choice and the browser's own answer.
// The script cannot import the module that owns the decision — a module script runs after
// the document is parsed, which is too late — so it repeats the rule. This test runs the
// copy that ships and holds it to the rule in theme.ts, so the two cannot drift apart.

const web = join(import.meta.dirname, '..', '..')
const entries = ['index.html', 'sidecar.html']

function html(entry: string): string {
  return readFileSync(join(web, entry), 'utf8')
}

function bootScript(entry: string): string {
  const match = html(entry).match(/<script id="theme-boot">([\s\S]*?)<\/script>/)
  assert.ok(match, `${entry} must carry the theme boot script`)
  return match[1]
}

function bootedDark(entry: string, stored: string | null, prefersDark: boolean): boolean {
  const classes = new Set<string>()
  vm.runInNewContext(bootScript(entry), {
    localStorage: { getItem: (key: string) => (key === THEME_STORAGE_KEY ? stored : null) },
    window: { matchMedia: (query: string) => ({ matches: query.includes('prefers-color-scheme: dark') ? prefersDark : false }) },
    document: {
      documentElement: {
        classList: {
          toggle: (name: string, on: boolean) => {
            if (on) classes.add(name)
            else classes.delete(name)
          },
        },
      },
    },
  })
  return classes.has('dark')
}

test('the boot script settles the scheme the same way the app does', () => {
  for (const entry of entries) {
    for (const stored of [null, 'system', 'light', 'dark', 'solarized', '']) {
      for (const prefersDark of [true, false]) {
        const expected = resolveDark(parseThemeMode(stored), prefersDark)
        assert.equal(
          bootedDark(entry, stored, prefersDark),
          expected,
          `${entry}: stored=${stored} prefersDark=${prefersDark}`,
        )
      }
    }
  }
})

test('the boot script runs in the head, before the app starts', () => {
  for (const entry of entries) {
    const source = html(entry)
    const boot = source.indexOf('id="theme-boot"')
    const app = source.indexOf('<script type="module"')
    assert.ok(boot >= 0 && app >= 0, `${entry} must carry both the boot script and the app`)
    assert.ok(boot < app, `${entry} must settle the theme before the app runs`)
    assert.ok(boot < source.indexOf('</head>'), `${entry} must settle the theme in the head`)
  }
})

test('a browser that refuses storage or media queries keeps the light scheme', () => {
  for (const entry of entries) {
    const classes = new Set<string>()
    vm.runInNewContext(bootScript(entry), {
      localStorage: {
        getItem: () => {
          throw new Error('storage refused')
        },
      },
      window: {
        matchMedia: () => {
          throw new Error('media queries refused')
        },
      },
      document: {
        documentElement: {
          classList: {
            toggle: (name: string, on: boolean) => {
              if (on) classes.add(name)
            },
          },
        },
      },
    })
    assert.equal(classes.has('dark'), false, `${entry} must not fail on a refused browser API`)
  }
})
