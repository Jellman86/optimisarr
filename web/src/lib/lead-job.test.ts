import { test } from 'node:test'
import assert from 'node:assert/strict'
import { pickLeadJob, type LeadCandidate } from './lead-job.ts'

function job(overrides: Partial<LeadCandidate> & { id: number }): LeadCandidate & { id: number } {
  return { status: 'Transcoding', remoteStage: null, workerName: null, progress: 0, ...overrides }
}

test('no live jobs means no lead job', () => {
  assert.equal(pickLeadJob([]), null)
})

test('an encode on this server beats one on a worker, however far along the worker is', () => {
  const lead = pickLeadJob([
    job({ id: 1, workerName: 'picard', remoteStage: 'Encoding', progress: 0.9 }),
    job({ id: 2, progress: 0.2 }),
  ])
  assert.equal(lead?.id, 2)
})

test('among local encodes the one furthest along wins', () => {
  const lead = pickLeadJob([job({ id: 1, progress: 0.3 }), job({ id: 2, progress: 0.7 }), job({ id: 3, progress: 0.5 })])
  assert.equal(lead?.id, 2)
})

test('a job that is only probing is shown when nothing is encoding', () => {
  const lead = pickLeadJob([job({ id: 4, status: 'Probing', progress: 0 })])
  assert.equal(lead?.id, 4)
})
