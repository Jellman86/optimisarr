import { test } from 'node:test'
import assert from 'node:assert/strict'
import { leadActivity, pickLeadJob, type LeadCandidate } from './lead-job.ts'

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

test('a returned candidate being checked is verification on this server, not an encode on the worker', () => {
  // Regression: after the Mac handed its encode back, the sidebar said "Now encoding · Scott's
  // MacBook Air" while the Mac itself said it was idle. The job still carries the worker's name.
  for (const status of ['AwaitingVerification', 'Verifying']) {
    const activity = leadActivity(job({ id: 5, status, workerName: "Scott's MacBook Air", progress: 0.4 }))
    assert.equal(activity.place, 'server')
    assert.equal(activity.from, "Scott's MacBook Air")
    assert.notEqual(activity.phase, 'encoding')
  }
})

test('each stage is named for what is happening', () => {
  const phase = (overrides: Partial<LeadCandidate>) => leadActivity(job({ id: 1, ...overrides })).phase
  assert.equal(phase({ status: 'Transcoding' }), 'encoding')
  assert.equal(phase({ status: 'Leased', remoteStage: 'Encoding', workerName: 'picard' }), 'encoding')
  assert.equal(phase({ status: 'Leased', remoteStage: 'FetchingSource', workerName: 'picard' }), 'sending')
  assert.equal(phase({ status: 'Leased', remoteStage: 'Delivering', workerName: 'picard' }), 'returning')
  assert.equal(phase({ status: 'Leased', remoteStage: 'Claimed', workerName: 'picard' }), 'starting')
  assert.equal(phase({ status: 'Probing' }), 'probing')
  assert.equal(phase({ status: 'Verifying' }), 'verifying')
  assert.equal(phase({ status: 'AwaitingVerification' }), 'waiting')
  assert.equal(phase({ status: 'Verifying', finalizing: true }), 'finalizing')
})

test('work a worker holds is placed on that worker, and a transfer between the two', () => {
  const encoding = leadActivity(job({ id: 1, status: 'Leased', remoteStage: 'Encoding', workerName: 'picard' }))
  assert.equal(encoding.place, 'worker')
  assert.equal(encoding.from, 'picard')
  const moving = leadActivity(job({ id: 1, status: 'Leased', remoteStage: 'Delivering', workerName: 'picard' }))
  assert.equal(moving.place, 'transfer')
  assert.equal(leadActivity(job({ id: 1 })).place, 'server')
  assert.equal(leadActivity(job({ id: 1 })).from, null)
})
