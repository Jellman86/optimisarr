import assert from 'node:assert/strict'
import test from 'node:test'
import { audioEncodingPresets, audioPresetBitrate, audioBitrateAfterCodecChange, audioEncodingMode } from './audio-encoding-presets.ts'

test('audio presets use codec-specific, increasing bitrate budgets without a gate threshold', () => {
  assert.deepEqual(audioEncodingPresets('aac').map(preset => preset.bitrate), [96, 128, 192, 256])
  assert.deepEqual(audioEncodingPresets('opus').map(preset => preset.bitrate), [96, 128, 160, 192])
  assert.deepEqual(audioEncodingPresets('mp3').map(preset => preset.bitrate), [128, 192, 256, 320])
  for (const codec of ['aac', 'opus', 'mp3']) {
    assert.equal(audioEncodingPresets(codec).length, 4)
    assert.equal(audioEncodingPresets(codec).some(preset => 'limit' in preset), false)
  }
})

test('changing format retains an explicitly selected encoding tier using the new codec budget', () => {
  assert.equal(audioPresetBitrate('aac', 'high'), 192)
  assert.equal(audioPresetBitrate('opus', 'high'), 160)
  assert.equal(audioPresetBitrate('mp3', 'high'), 256)
})

test('unknown and copied formats have no encoding presets', () => {
  assert.deepEqual(audioEncodingPresets('copy'), [])
  assert.deepEqual(audioEncodingPresets('flac'), [])
  assert.deepEqual(audioEncodingPresets('constructor'), [])
  assert.equal(audioPresetBitrate('copy', 'high'), undefined)
})

test('loaded defaults and custom budgets survive a format change until a tier is explicitly chosen', () => {
  assert.equal(audioBitrateAfterCodecChange('aac', 'mp3', null, null), null)
  assert.equal(audioBitrateAfterCodecChange('aac', 'mp3', 128, null), 128)
  assert.equal(audioBitrateAfterCodecChange('aac', 'mp3', 173, 'custom'), 173)
  assert.equal(audioBitrateAfterCodecChange('aac', 'mp3', 192, 'high'), 256)
  assert.equal(audioBitrateAfterCodecChange('aac', 'mp3', 173, 'high'), 173)
  assert.equal(audioBitrateAfterCodecChange('aac', 'copy', 192, 'high'), 192)
})

test('preset labels derive from actual bitrate while explicit Custom and null defaults stay distinct', () => {
  assert.equal(audioEncodingMode('aac', null, null, 128), 'default')
  assert.equal(audioEncodingMode('aac', null, 'custom', 128), 'default')
  assert.equal(audioEncodingMode('aac', 128, null, 128), 'default')
  assert.equal(audioEncodingMode('aac', 160, null, 160), 'default')
  assert.equal(audioEncodingMode('aac', 96, null, 96), 'default')
  assert.equal(audioEncodingMode('aac', 128, 'balanced', 128), 'balanced')
  assert.equal(audioEncodingMode('aac', 173, null, 128), 'custom')
  assert.equal(audioEncodingMode('aac', 128, 'custom', 128), 'custom')
})
