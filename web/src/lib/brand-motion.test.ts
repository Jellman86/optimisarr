import { strict as assert } from 'node:assert'
import { test } from 'node:test'
import { createBrandMotion } from './brand-motion.ts'
import { createBrandGeometry } from './brand-geometry.ts'

test('rest holds every slice fixed while the invisible light completes a 32-second orbit', () => {
  const motion = createBrandMotion()
  const start = motion.lightPhase
  for (let i = 0; i < 960; i++) motion.step(1 / 30, false)
  assert.deepEqual(motion.angles, Array(15).fill(0))
  assert.equal(motion.opening, 0)
  assert.ok(Math.abs(motion.lightPhase - start) < 1e-10)
})

test('work progresses through the slices while the bottom remains fixed', () => {
  const motion = createBrandMotion()
  for (let i = 0; i < 100; i++) motion.step(1 / 30, true)
  assert.equal(motion.angles[0], 0)
  assert.equal(motion.velocities[0], 0)
  assert.ok(motion.angles[1] > motion.angles[14])
  assert.ok(motion.angles[14] > 0)
  assert.ok(motion.opening > .99)
})

test('arbitrarily interrupted transitions preserve position and velocity, then close seamlessly', () => {
  const motion = createBrandMotion()
  for (let i = 0; i < 200; i++) {
    motion.step(.031, true)
    const before = [...motion.angles, ...motion.velocities, motion.opening, motion.openingVelocity, motion.lightPhase]
    motion.step(0, false)
    motion.step(0, true)
    const after = [...motion.angles, ...motion.velocities, motion.opening, motion.openingVelocity, motion.lightPhase]
    assert.ok(after.every((v, j) => Math.abs(v - before[j]) < 1e-11))
  }
  for (let i = 0; i < 600; i++) motion.step(1 / 30, false)
  assert.equal(motion.mode, 'rest')
  assert.ok(motion.opening < 1e-8)
  for (const angle of motion.angles) assert.ok(Math.abs(Math.sin(angle)) < 1e-8)
})

test('the fifteen immutable meshes partition the beveled cube without missing caps or volume', () => {
  const { slices, shell } = createBrandGeometry()
  assert.equal(slices.length, 15)
  function volume(data: Float32Array) {
    let result = 0
    for (let i = 0; i < data.length; i += 18) {
      const [ax, ay, az] = data.slice(i, i + 3)
      const [bx, by, bz] = data.slice(i + 6, i + 9)
      const [cx, cy, cz] = data.slice(i + 12, i + 15)
      result += (ax * (by * cz - bz * cy) + ay * (bz * cx - bx * cz) + az * (bx * cy - by * cx)) / 6
    }
    return result
  }
  assert.ok(volume(shell) > 7.98 && volume(shell) < 8)
  assert.ok(Math.abs(slices.reduce((v, s) => v + volume(s), 0) - volume(shell)) < 1e-6)
  for (const slice of slices) {
    const edges = new Map<string, number>()
    for (let i = 0; i < slice.length; i += 18) {
      const points = [0, 6, 12].map(offset => [...slice.slice(i + offset, i + offset + 3)].map(v => String(Math.round(v * 1e6))).join(','))
      for (let j = 0; j < 3; j++) {
        const key = [points[j], points[(j + 1) % 3]].sort().join('|')
        edges.set(key, (edges.get(key) ?? 0) + 1)
      }
    }
    assert.ok([...edges.values()].every(count => count === 2), 'each mesh must be closed')
  }
})
