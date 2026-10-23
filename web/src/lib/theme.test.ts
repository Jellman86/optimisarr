import { test } from 'node:test'
import assert from 'node:assert/strict'
import { nextThemeMode, parseThemeMode, resolveDark, type ThemeMode } from './theme.ts'

test('a stored scheme is honoured and anything else falls back to the system', () => {
  assert.equal(parseThemeMode('dark'), 'dark')
  assert.equal(parseThemeMode('light'), 'light')
  assert.equal(parseThemeMode(null), 'system')
  assert.equal(parseThemeMode(undefined), 'system')
  assert.equal(parseThemeMode('system'), 'system')
  assert.equal(parseThemeMode('solarized'), 'system')
  assert.equal(parseThemeMode(''), 'system')
})

test('the system decides only while the page is following it', () => {
  assert.equal(resolveDark('system', true), true)
  assert.equal(resolveDark('system', false), false)
  assert.equal(resolveDark('dark', false), true)
  assert.equal(resolveDark('light', true), false)
})

test('the theme button reaches every setting from every setting', () => {
  for (const prefersDark of [true, false]) {
    for (const start of ['system', 'light', 'dark'] as ThemeMode[]) {
      const seen = new Set<ThemeMode>([start])
      let mode = start
      for (let step = 0; step < 3; step++) {
        mode = nextThemeMode(mode, prefersDark)
        seen.add(mode)
      }
      assert.deepEqual([...seen].sort(), ['dark', 'light', 'system'], `prefersDark=${prefersDark} start=${start}`)
      assert.equal(mode, start, `the cycle returns to where it started (prefersDark=${prefersDark} start=${start})`)
    }
  }
})

test('the first click away from the system always changes the page', () => {
  // A light system pins dark first, and a dark system pins light first, so no click can
  // leave the page looking exactly as it did.
  assert.equal(nextThemeMode('system', false), 'dark')
  assert.equal(nextThemeMode('system', true), 'light')
  assert.notEqual(resolveDark(nextThemeMode('system', false), false), resolveDark('system', false))
  assert.notEqual(resolveDark(nextThemeMode('system', true), true), resolveDark('system', true))
})

test('the cycle hands the page back to the system after the two pinned schemes', () => {
  assert.deepEqual(walk('system', false), ['dark', 'light', 'system'])
  assert.deepEqual(walk('system', true), ['light', 'dark', 'system'])
})

function walk(start: ThemeMode, prefersDark: boolean): ThemeMode[] {
  const modes: ThemeMode[] = []
  let mode = start
  for (let step = 0; step < 3; step++) {
    mode = nextThemeMode(mode, prefersDark)
    modes.push(mode)
  }
  return modes
}
