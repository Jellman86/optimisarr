import { strict as assert } from 'node:assert'
import { test } from 'node:test'
import { deepFieldGalaxies, foregroundStars, galaxyPlacement } from './stellar-sky.ts'

test('the field is mostly galaxies of every kind, as in the Hubble Deep Field', () => {
  const galaxies = deepFieldGalaxies()
  const kinds = new Set(galaxies.flat().map((galaxy) => galaxy.kind))
  assert.deepEqual([...kinds].sort(), ['edge-on', 'elliptical', 'irregular', 'spiral'])
  const spiked = foregroundStars().flat().filter((star) => star.brightness > 0.6).length
  assert.ok(galaxies.flat().length > spiked, 'only a few foreground stars carry spikes')
})

test('near galaxies pass faster than far ones, so the cube reads as travelling', () => {
  const galaxies = deepFieldGalaxies().flat()
  const near = galaxies.reduce((a, b) => (a.depth < b.depth ? a : b))
  const far = galaxies.reduce((a, b) => (a.depth > b.depth ? a : b))
  const travel = (galaxy: typeof near) => {
    const [u0, v0] = galaxyPlacement(galaxy, 0), [u1, v1] = galaxyPlacement(galaxy, 0.05)
    return Math.hypot(u1 - u0, v1 - v0)
  }
  assert.ok(near.depth < far.depth)
  assert.ok(travel(near) > travel(far) * 1.5)
  assert.ok(near.size > far.size, 'nearer galaxies also look larger')
})

test('a galaxy only wraps around once it has fully left its face', () => {
  for (const galaxy of deepFieldGalaxies().flat()) {
    const radius = galaxy.size / 2
    let previous = galaxyPlacement(galaxy, 0)
    for (let drift = 0.01; drift < 40; drift += 0.01) {
      const next = galaxyPlacement(galaxy, drift)
      if (Math.hypot(next[0] - previous[0], next[1] - previous[1]) > 0.5) {
        for (const [u, v] of [previous, next]) {
          const outside = u < -radius || u > 1 + radius || v < -radius || v > 1 + radius
          assert.ok(outside, `wrapped at (${u.toFixed(2)}, ${v.toFixed(2)}) while visible`)
        }
      }
      previous = next
    }
  }
})
