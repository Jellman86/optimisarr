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
  assert.ok(chart.bars[1].fraction > 0.9 && chart.bars[1].fraction <= 1)
})

test('an empty month has no peak and no bars above zero', () => {
  const chart = savingsBars([day('2026-09-24', 0), day('2026-09-25', 0)])
  assert.equal(chart.peakBytes, 0)
  assert.ok(chart.bars.every(bar => bar.fraction === 0))
})

const GB = 1024 ** 3
const MB = 1024 ** 2

test('the scale rounds up to a whole step so its gridlines read as plain sizes', () => {
  // A 42 GB peak draws against 0, 10, 20, 30, 40, 50 GB, not against a bare "42 GB" at the top.
  const chart = savingsBars([day('2026-09-24', 1 * GB), day('2026-09-25', 42 * GB)])
  assert.equal(chart.scaleBytes, 50 * GB)
  assert.deepEqual(chart.ticks, [0, 10 * GB, 20 * GB, 30 * GB, 40 * GB, 50 * GB])
  assert.equal(chart.bars[1].fraction, 42 / 50)
})

test('scale steps come in the unit the sizes are written in', () => {
  assert.deepEqual(savingsBars([day('2026-09-25', 700 * MB)]).ticks, [0, 200 * MB, 400 * MB, 600 * MB, 800 * MB])
  assert.deepEqual(savingsBars([day('2026-09-25', 3 * GB)]).ticks, [0, 1 * GB, 2 * GB, 3 * GB])
  assert.deepEqual(savingsBars([day("2026-09-25", 10 * GB)]).ticks, [0, 2 * GB, 4 * GB, 6 * GB, 8 * GB, 10 * GB])
})

test('the daily average counts the quiet days, because it is an average per day', () => {
  const chart = savingsBars([day('2026-09-23', 0), day('2026-09-24', 0), day('2026-09-25', 30 * GB)])
  assert.equal(chart.averageBytes, 10 * GB)
  assert.equal(chart.averageFraction, 10 / 30)
})

test('week marks count back from today so the last one is always today', () => {
  const days = Array.from({ length: 30 }, (_, index) => day(`2026-09-${String(index + 1).padStart(2, '0')}`, 0))
  assert.deepEqual(savingsBars(days).weekMarks, [1, 8, 15, 22, 29])
})
