import assert from 'node:assert/strict'
import test from 'node:test'
import { savingsBars } from './savings-chart.ts'

const day = (date: string, bytesSaved: number, files = bytesSaved > 0 ? 1 : 0) => ({ date, bytesSaved, files })

test('bars scale to the busiest day and keep every day in order', () => {
  const chart = savingsBars([day('2026-09-23', 0), day('2026-09-24', 500), day('2026-09-25', 1000)])
  assert.deepEqual(chart.bars.map(bar => bar.date), ['2026-09-23', '2026-09-24', '2026-09-25'])
  assert.deepEqual(chart.bars.map(bar => bar.fraction), [0, 0.5, 1])
  assert.equal(chart.peakBytes, 1000)
  assert.equal(chart.totalBytes, 1500)
  assert.equal(chart.totalFiles, 2)
})

test('a small but real day stays visible next to a huge one', () => {
  const chart = savingsBars([day('2026-09-24', 1), day('2026-09-25', 1_000_000)])
  assert.ok(chart.bars[0].fraction > 0.01, 'one saved byte must still draw a sliver, not disappear')
  assert.equal(chart.bars[1].fraction, 1)
})

test('an empty month has no peak and no bars above zero', () => {
  const chart = savingsBars([day('2026-09-24', 0), day('2026-09-25', 0)])
  assert.equal(chart.peakBytes, 0)
  assert.ok(chart.bars.every(bar => bar.fraction === 0))
})
