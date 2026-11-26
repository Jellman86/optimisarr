import assert from 'node:assert/strict'
import test from 'node:test'
import { PSEUDO_EXPANSION, pseudoLocalise, pseudoString } from './pseudo-locale.ts'

const visibleLength = (value: string) => [...value].length

test('a pseudo string is at least forty percent longer than its English source', () => {
  for (const source of ['Scan', 'Pause queue', 'Replace every verified ready output, moving each original to quarantine first.']) {
    const expanded = pseudoString(source)
    assert.ok(
      visibleLength(expanded) >= Math.ceil(visibleLength(source) * (1 + PSEUDO_EXPANSION)),
      `${JSON.stringify(expanded)} is not 40% longer than ${JSON.stringify(source)}`,
    )
  }
})

test('placeholders survive untouched so interpolation still works', () => {
  const expanded = pseudoString('Clear completed ({count}) on {path}')
  assert.match(expanded, /\{count\}/)
  assert.match(expanded, /\{path\}/)
  assert.doesNotMatch(expanded, /Clear/)
})

test('letters are accented so an untranslated hard-coded string stands out', () => {
  assert.notEqual(pseudoString('Queue'), 'Queue')
  assert.doesNotMatch(pseudoString('Queue'), /[A-Za-z]{2}/)
})

test('the padding wraps as words rather than forcing one unbreakable run', () => {
  const expanded = pseudoString('Remove failed and cancelled jobs, their diagnostics, and retained outputs.')
  const longestWord = Math.max(...expanded.split(/\s+/).map(visibleLength))
  assert.ok(longestWord <= 14, `longest run is ${longestWord} characters`)
})

test('line breaks in a confirmation stay where the English put them', () => {
  assert.equal(pseudoString('First line?\n\nSecond line.').split('\n').length, 3)
})

test('every message in a tree is expanded and the shape is kept', () => {
  const messages = { nav: { queue: 'Queue', settings: 'Settings' }, empty: '' }
  const pseudo = pseudoLocalise(messages)
  assert.deepEqual(Object.keys(pseudo.nav), ['queue', 'settings'])
  assert.notEqual(pseudo.nav.queue, 'Queue')
  assert.equal(pseudo.empty, '')
})
