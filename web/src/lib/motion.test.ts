import { test } from 'node:test'
import assert from 'node:assert/strict'
import { parseCssTime } from './motion.ts'

test('parseCssTime reads a motion token in milliseconds or seconds', () => {
  assert.equal(parseCssTime('180ms'), 180)
  assert.equal(parseCssTime(' 0.3s '), 300)
  assert.equal(parseCssTime('.12s'), 120)
})

test('parseCssTime treats a collapsed or unreadable token as no motion', () => {
  assert.equal(parseCssTime('0s'), 0)
  assert.equal(parseCssTime(''), 0)
  assert.equal(parseCssTime('fast'), 0)
  assert.equal(parseCssTime('-50ms'), 0)
})
