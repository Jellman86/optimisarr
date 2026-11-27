import { test } from 'node:test'
import assert from 'node:assert/strict'
import { jobPercent, jobStep, isWorkingJob, isJobSuspended, isJobStageActive, jobLocation, verificationPhase, localWorkloadCapacity, encoderQualityControl, qualityChipLabel, jobStageTone } from './job-presentation.ts'

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

test('encoder quality controls read as their short uppercase codes, whatever case the server used', () => {
  assert.equal(encoderQualityControl('icq'), 'ICQ')
  assert.equal(encoderQualityControl('ICQ'), 'ICQ')
  assert.equal(encoderQualityControl('cq'), 'CQ')
  assert.equal(encoderQualityControl('QP'), 'QP')
  assert.equal(encoderQualityControl('vt-q'), 'VT-Q')
  assert.equal(encoderQualityControl('crf'), 'CRF')
  assert.equal(encoderQualityControl('Crf'), 'CRF')
  assert.equal(encoderQualityControl('AdaptiveVmaf'), null)
  assert.equal(encoderQualityControl('Bitrate'), null)
  assert.equal(encoderQualityControl(null), null)
})

test('the quality chip names the control the encoder used, and CRF only when nothing narrower is known', () => {
  assert.equal(qualityChipLabel('icq', 24), 'ICQ 24')
  assert.equal(qualityChipLabel('CQ', 22), 'CQ 22')
  assert.equal(qualityChipLabel('VT-Q', 20), 'VT-Q 20')
  assert.equal(qualityChipLabel('AdaptiveVmaf', 24), 'CRF 24')
  assert.equal(qualityChipLabel('Bitrate', 4000), '4000')
  assert.equal(qualityChipLabel(null, 24), 'CRF 24')
})

test('only work that is moving is drawn as live; suspended encodes pause, failures fail, and everything else waits', () => {
  const suspended = { runningEncodesSuspended: true, manualPauseMode: 'suspended' }
  for (const status of ['Probing', 'Transcoding', 'Verifying']) assert.equal(jobStageTone({ status }), 'live')
  assert.equal(jobStageTone({ status: 'Transcoding' }, suspended), 'paused')
  assert.equal(jobStageTone({ status: 'Leased', remoteStage: 'Encoding' }, suspended), 'live')
  assert.equal(jobStageTone({ status: 'ReadyToReplace', finalizing: true }), 'live')
  assert.equal(jobStageTone({ status: 'Failed' }), 'failed')
  for (const status of ['Queued', 'AwaitingVerification', 'ReadyToReplace', 'Completed', 'Cancelled']) {
    assert.equal(jobStageTone({ status }), 'waiting', status)
  }
})
