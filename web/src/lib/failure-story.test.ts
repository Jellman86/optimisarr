import { test } from 'node:test'
import assert from 'node:assert/strict'
import { failedCheckNames, primaryFailureCause } from './failure-story.ts'

const pass = (name: string) => ({ name, outcome: 'Passed' })
const fail = (name: string) => ({ name, outcome: 'Failed' })

test('a damaged source leads even when the size and quality gates also failed', () => {
  const checks = [pass('Decode health'), fail('Size saving'), fail('Source video timeline'), fail('Perceptual quality (VMAF)')]
  assert.equal(primaryFailureCause(checks), 'damaged_source')
})

test('a source rejected before encoding is recognised from its message alone', () => {
  const message = 'Not encoded. Verification failed before encoding: Source video timeline: the picture ends early.'
  assert.equal(primaryFailureCause([], message), 'damaged_source')
})

test('a broken output explains its bad quality score, so it leads', () => {
  assert.equal(primaryFailureCause([fail('Perceptual quality (VMAF)'), fail('Tail integrity')]), 'broken_output')
})

test('quality, sound and retained content outrank the size rule', () => {
  assert.equal(primaryFailureCause([fail('Size saving'), fail('Perceptual quality (VMAF)')]), 'looks_worse')
  assert.equal(primaryFailureCause([fail('Image quality (SSIM)')]), 'looks_worse')
  assert.equal(primaryFailureCause([fail('Perceptual audio quality (Zimtohrli)')]), 'sounds_worse')
  assert.equal(primaryFailureCause([fail('Size saving'), fail('Subtitle tracks')]), 'lost_content')
})

test('the size rule alone reads as not small enough, and a predicted miss keeps its own story', () => {
  assert.equal(primaryFailureCause([pass('Decode health'), fail('Size saving')]), 'too_big')
  assert.equal(primaryFailureCause(null, 'Size saving prediction: samples predict 112%.'), 'size_predicted')
})

test('nothing specific known falls back to the server category', () => {
  assert.equal(primaryFailureCause([pass('Size saving')], 'ffmpeg exited with code 1'), null)
  assert.equal(primaryFailureCause(undefined, null), null)
})

test('failed gate names are listed in report order', () => {
  assert.deepEqual(failedCheckNames([fail('Size saving'), pass('Duration'), fail('Source video timeline')]), ['Size saving', 'Source video timeline'])
})
