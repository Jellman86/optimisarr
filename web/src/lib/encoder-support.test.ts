import assert from 'node:assert/strict'
import test from 'node:test'
import { encoderFamily, encoderIgnores, encoderName } from './encoder-support.ts'

test('the encoder is known only when one fixed encoder does every job', () => {
  assert.equal(encoderFamily('Auto', 'hevc', false), null)
  assert.equal(encoderFamily('IntelQsv', 'hevc', true), null)
  assert.equal(encoderFamily(undefined, 'hevc', false), null)
  assert.equal(encoderFamily('Cpu', 'hevc', false), 'x26x')
  assert.equal(encoderFamily('Cpu', 'AV1', false), 'svtav1')
  assert.equal(encoderFamily('NvidiaNvenc', 'av1', false), 'nvenc')
  assert.equal(encoderFamily('IntelQsv', 'hevc', false), 'qsv')
  assert.equal(encoderFamily('Vaapi', 'h264', false), 'vaapi')
})

test('each encoder ignores exactly what the server would not pass to it', () => {
  const settings = ['contentTune', 'maxBitrate', 'minBitrate', 'adaptiveQuantisation'] as const
  const ignoredBy = (family: Parameters<typeof encoderIgnores>[0]) => settings.filter((setting) => encoderIgnores(family, setting))

  assert.deepEqual(ignoredBy('x26x'), [])
  assert.deepEqual(ignoredBy('nvenc'), ['contentTune', 'minBitrate'])
  assert.deepEqual(ignoredBy('svtav1'), ['contentTune', 'maxBitrate', 'minBitrate'])
  assert.deepEqual(ignoredBy('qsv'), settings)
  assert.deepEqual(ignoredBy('vaapi'), settings)
})

test('nothing is called ignored while the encoder is not known', () => {
  assert.equal(encoderIgnores(null, 'contentTune'), false)
  assert.equal(encoderName('qsv'), 'Intel Quick Sync')
})
