import { test } from 'node:test'
import assert from 'node:assert/strict'
import { jobPercent, jobStep, isWorkingJob, isJobSuspended, isJobStageActive, jobLocation, verificationPhase, localWorkloadCapacity } from './job-presentation.ts'

test('location and verification route describe the current attempt', () => {
  assert.equal(jobLocation({ status: 'Leased', remoteStage: 'Encoding' }), 'worker')
  assert.equal(jobLocation({ status: 'Leased', remoteStage: 'Delivering' }), 'transfer')
  assert.equal(jobLocation({ status: 'AwaitingVerification', sidecarVerification: true }), 'container')
  assert.equal(verificationPhase({ status: 'AwaitingVerification', sidecarVerification: true }), 'waiting')
  assert.equal(verificationPhase({ status: 'Verifying', sidecarVerification: true }), 'evidence')
  assert.equal(verificationPhase({ status: 'Verifying', sidecarVerification: false }), 'media')
})

test('local and remote encodes never claim completion before the process exits', () => {
  assert.equal(jobPercent({ status: 'Transcoding', progress: .9999 }), 99)
  assert.equal(jobPercent({ status: 'Leased', remoteStage: 'Encoding', progress: 1 }), 99)
  assert.equal(jobPercent({ status: 'Transcoding', progress: -.1 }), 0)
  assert.equal(jobPercent({ status: 'Transcoding', progress: NaN }), null)
})

test('unknown transfer and probe progress stays indeterminate instead of borrowing encode progress', () => {
  assert.equal(jobPercent({ status: 'Leased', remoteStage: 'Delivering', progress: .8 }), null)
  assert.equal(jobPercent({ status: 'Probing', progress: 0 }), null)
  assert.equal(jobPercent({ status: 'Probing', progress: .32 }), 32)
  assert.equal(jobPercent({ status: 'Verifying', progress: .18 }), 18)
})

test('stage markers distinguish adaptive preparation, delivery, verification and completed replacement', () => {
  assert.equal(jobStep({ status: 'Leased', remoteStage: 'Measuring' }), 0)
  assert.equal(jobStep({ status: 'Leased', remoteStage: 'Encoding' }), 1)
  assert.equal(jobStep({ status: 'Leased', remoteStage: 'Delivering' }), 2)
  assert.equal(jobStep({ status: 'AwaitingVerification' }), 2)
  assert.equal(jobStep({ status: 'ReadyToReplace' }), 3)
  assert.equal(jobStep({ status: 'Completed' }), 4)
  assert.equal(jobStep({ status: 'Failed' }), null)
  assert.equal(isWorkingJob({ status: 'ReadyToReplace' }), false)
  assert.equal(isWorkingJob({ status: 'ReadyToReplace', finalizing: true }), true)
})

test('only confirmed full suspension labels a local encode as paused', () => {
  const status = { runningEncodesSuspended: true, manualPauseMode: 'suspended' }
  assert.equal(isJobSuspended({ status: 'Transcoding' }, status), true)
  assert.equal(isJobSuspended({ status: 'Leased' }, status), false)
  assert.equal(isJobSuspended({ status: 'Verifying' }, status), false)
  assert.equal(isJobSuspended({ status: 'Transcoding' }, { ...status, manualPauseMode: 'partial' }), false)
})

test('only actively running stages pulse; waiting and suspended work stays still', () => {
  const suspended = { runningEncodesSuspended: true, manualPauseMode: 'suspended' }
  assert.equal(isJobStageActive({ status: 'Probing' }, null), true)
  assert.equal(isJobStageActive({ status: 'Transcoding' }, null), true)
  assert.equal(isJobStageActive({ status: 'Verifying' }, null), true)
  assert.equal(isJobStageActive({ status: 'Leased', remoteStage: 'Encoding' }, suspended), true)
  assert.equal(isJobStageActive({ status: 'Transcoding' }, suspended), false)
  assert.equal(isJobStageActive({ status: 'AwaitingVerification' }, null), false)
  assert.equal(isJobStageActive({ status: 'ReadyToReplace' }, null), false)
  assert.equal(isJobStageActive({ status: 'ReadyToReplace', finalizing: true }, null), true)
  assert.equal(isJobStageActive({ status: 'Completed' }, null), false)
})

test('local slot totals exclude workers and safe replacement capacity', () => {
  assert.equal(localWorkloadCapacity({ maxConcurrentJobs: 1 }), 1)
  assert.equal(localWorkloadCapacity({ maxConcurrentJobs: 1, workloadLanes: [
    { lane: 'Video', capacity: 1 }, { lane: 'NonVideo', capacity: 1 },
    { lane: 'Evidence', capacity: 2 }, { lane: 'Finalization', capacity: 2 },
    { lane: 'Workers', capacity: 8 },
  ] }), 4)
})
