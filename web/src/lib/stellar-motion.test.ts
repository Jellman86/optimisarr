import { strict as assert } from 'node:assert'
import { test } from 'node:test'
import { createStellarMotion } from './stellar-motion.ts'

const advance = (motion: ReturnType<typeof createStellarMotion>, seconds: number, working: boolean) => {
  for (let i = 0; i < seconds * 60; i++) motion.step(1 / 60, working)
}
const snapshot = (motion: ReturnType<typeof createStellarMotion>) =>
  [motion.time, motion.activity, motion.flow, motion.drift]

test('idle keeps every star at home while the sky slowly drifts', () => {
  const motion = createStellarMotion()
  advance(motion, 12, false)
  assert.equal(motion.mode, 'rest')
  assert.equal(motion.activity, 0)
  assert.equal(motion.flow, 0)
  assert.ok(motion.drift > .4, 'the nebula keeps a slow drift at rest')
  assert.ok(motion.time > 11)
})

test('work streams the stars into the front corner and settles back to rest', () => {
  const motion = createStellarMotion()
  advance(motion, 5, true)
  assert.equal(motion.mode, 'cycle')
  assert.ok(motion.activity > .95)
  assert.ok(motion.flow > 1, 'stars complete at least one pass while working')
  advance(motion, .1, false)
  assert.equal(motion.mode, 'settle')
  advance(motion, 6, false)
  assert.equal(motion.mode, 'rest')
  assert.equal(motion.activity, 0, 'rest is exact, so the icon returns to its home frame')
})

test('a state notification alone never moves the stars', () => {
  const motion = createStellarMotion()
  advance(motion, 3, true)
  const before = snapshot(motion)
  motion.step(0, false)
  assert.equal(motion.mode, 'settle')
  motion.step(0, true)
  assert.equal(motion.mode, 'cycle')
  assert.deepEqual(snapshot(motion), before)
})

test('interrupted activity settles from every point in the work cycle', () => {
  for (let t = 0; t < 12; t += .5) {
    const motion = createStellarMotion()
    advance(motion, t, true)
    advance(motion, 6, false)
    assert.equal(motion.mode, 'rest', `settled after ${t}s of work`)
    assert.equal(motion.activity, 0)
  }
})
