import { strict as assert } from 'node:assert'
import { test } from 'node:test'
import { createStellarMotion } from './stellar-motion.ts'

const advance = (motion: ReturnType<typeof createStellarMotion>, seconds: number, working: boolean) => {
  for (let i = 0; i < seconds * 60; i++) motion.step(1 / 60, working)
}

test('idle keeps the stellar geometry fixed while light and stars drift', () => {
  const motion = createStellarMotion()
  const pose = { ...motion.pose }
  advance(motion, 12, false)
  assert.deepEqual(motion.pose, pose)
  assert.ok(motion.lightTime > 11)
  assert.ok(motion.phase > 17)
  assert.equal(motion.mode, 'rest')
})

test('work precesses and occasionally becomes indented before returning outward', () => {
  const motion = createStellarMotion()
  advance(motion, 17, true)
  assert.ok(motion.pose.depth < -.99)
  assert.notEqual(motion.pose.yaw, .082)
  advance(motion, 12, true)
  assert.ok(motion.pose.depth > .99)
})

test('interrupted activity preserves position and velocity and settles from every cycle phase', () => {
  for (let t = 0; t < 36; t += .5) {
    const motion = createStellarMotion()
    advance(motion, t, true)
    const before = [...Object.values(motion.pose), ...Object.values(motion.velocity), motion.phase]
    motion.step(0, false)
    motion.step(0, true)
    assert.deepEqual([...Object.values(motion.pose), ...Object.values(motion.velocity), motion.phase], before)
    advance(motion, 10, false)
    assert.equal(motion.mode, 'rest')
    assert.deepEqual(motion.pose, { yaw: .082, pitch: 0, depth: 1 })
  }
})
