import { test } from 'node:test'
import assert from 'node:assert/strict'
import { actualPixelsZoom, nextUnratedName, sampleForKey, settingChange } from './calibration-presentation.ts'

const result = { recommendedQuality: 28, recommendedProfile: null, currentQuality: 24, currentProfile: 'ConservativeHevc' }

test('quality-only video change keeps the saved preset on both sides', () => {
  assert.deepEqual(settingChange(result), {
    now: { profile: null, quality: 24 },
    after: { profile: null, quality: 28 },
    unchanged: false,
  })
})

test('preset comparison shows the preset that Apply would adopt', () => {
  assert.deepEqual(settingChange({ ...result, recommendedProfile: 'ExperimentalAv1', recommendedQuality: 24 }), {
    now: { profile: 'ConservativeHevc', quality: 24 },
    after: { profile: 'ExperimentalAv1', quality: 24 },
    unchanged: false,
  })
})

test('recommending the saved setting is reported as unchanged', () => {
  assert.equal(settingChange({ ...result, recommendedQuality: 24 })?.unchanged, true)
  assert.equal(settingChange({ ...result, recommendedProfile: 'ConservativeHevc', recommendedQuality: 24 })?.unchanged, true)
})

test('audio and image results have no preset and no change without a recommendation', () => {
  assert.deepEqual(settingChange({ recommendedQuality: 128, recommendedProfile: null, currentQuality: 160, currentProfile: null })?.after, { profile: null, quality: 128 })
  assert.equal(settingChange({ ...result, recommendedQuality: null }), null)
})

test('next unrated sample follows the active one and wraps around', () => {
  const names = ['A', 'B', 'C', 'D']
  assert.equal(nextUnratedName(names, { A: 'Acceptable' }, 'A'), 'B')
  assert.equal(nextUnratedName(names, { A: 'Acceptable', C: 'Acceptable', D: 'Acceptable' }, 'C'), 'B')
  assert.equal(nextUnratedName(names, { B: 'Acceptable' }, 'ORIGINAL'), 'A')
  assert.equal(nextUnratedName(names, { A: 'Acceptable', B: 'Acceptable', C: 'Acceptable', D: 'Acceptable' }, 'A'), null)
})

test('next unrated sample never points at the sample already on screen', () => {
  assert.equal(nextUnratedName(['A', 'B'], { B: 'Acceptable' }, 'A'), null)
})

test('actual pixels zoom undoes the fit-to-view scale', () => {
  assert.equal(actualPixelsZoom(3840, 2160, 960, 540), 4)
  // Letterboxed: height limits a tall image.
  assert.equal(actualPixelsZoom(1000, 2000, 1000, 500), 4)
  // A small image is enlarged to fit, so 1:1 shrinks it back.
  assert.equal(actualPixelsZoom(480, 270, 960, 540), 0.5)
  assert.equal(actualPixelsZoom(0, 0, 960, 540), null)
  assert.equal(actualPixelsZoom(1920, 1080, 0, 0), null)
})

test('sample shortcut keys pick the original with O and a candidate by its letter', () => {
  const names = ['ORIGINAL', 'A', 'B', 'C', 'D']
  assert.equal(sampleForKey('o', names), 'ORIGINAL')
  assert.equal(sampleForKey('O', names), 'ORIGINAL')
  assert.equal(sampleForKey('b', names), 'B')
  assert.equal(sampleForKey('D', names), 'D')
})

test('sample shortcut keys ignore letters without a sample and named keys', () => {
  const names = ['ORIGINAL', 'A', 'B']
  assert.equal(sampleForKey('e', names), null)
  assert.equal(sampleForKey('Enter', names), null)
  assert.equal(sampleForKey(' ', names), null)
  assert.equal(sampleForKey('o', ['A', 'B']), null)
})
