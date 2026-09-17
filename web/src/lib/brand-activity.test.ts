import { strict as assert } from 'node:assert'
import { test } from 'node:test'
import { hasBrandActivity } from './brand-activity.ts'

const queue = { runningJobs: 0, suspendedEncodeCount: 0 }
const job = (status: string, workerName: string | null = null) => ({ status, workerName })

test('queued and completed jobs leave the light at rest', () => {
  assert.equal(hasBrandActivity(queue, [job('Queued'), job('Completed')]), false)
})
test('local dispatch, probing and verification excite the light', () => {
  assert.equal(hasBrandActivity({ ...queue, runningJobs: 1 }, []), true)
  for (const status of ['Probing', 'Transcoding', 'Verifying']) {
    assert.equal(hasBrandActivity(queue, [job(status)]), true)
  }
})
test('remote work excites the light even when the local dispatcher is idle', () => {
  assert.equal(hasBrandActivity(queue, [job('Leased', 'Mac mini')]), true)
  assert.equal(hasBrandActivity(queue, [job('Leased')]), true)
})
test('suspended encodes rest but independent remote or verification work stays active', () => {
  const paused = { runningJobs: 1, suspendedEncodeCount: 1 }
  assert.equal(hasBrandActivity(paused, [job('Transcoding')]), false)
  assert.equal(hasBrandActivity(paused, [job('Transcoding'), job('Leased', 'Mac mini')]), true)
  assert.equal(hasBrandActivity(paused, [job('Verifying')]), true)
})
