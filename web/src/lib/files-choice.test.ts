import assert from 'node:assert/strict'
import test from 'node:test'
import { filesChoice, flagsFor, hasContradictoryFlags, savedElsewherePath } from './files-choice.ts'

test('filesChoice reads the two saved switches as one choice', () => {
  assert.equal(filesChoice({ autoReplace: false, moveOnComplete: false }), 'ask')
  assert.equal(filesChoice({ autoReplace: true, moveOnComplete: false }), 'auto')
  assert.equal(filesChoice({ autoReplace: false, moveOnComplete: true }), 'elsewhere')
})

test('a library saved with both switches on reads as saving elsewhere, because that is what ran', () => {
  const both = { autoReplace: true, moveOnComplete: true }

  assert.equal(filesChoice(both), 'elsewhere')
  assert.equal(hasContradictoryFlags(both), true)
  assert.equal(hasContradictoryFlags({ autoReplace: true, moveOnComplete: false }), false)
})

test('flagsFor never turns both switches on, and round-trips every choice', () => {
  for (const choice of ['ask', 'auto', 'elsewhere'] as const) {
    const flags = flagsFor(choice)
    assert.equal(hasContradictoryFlags(flags), false)
    assert.equal(filesChoice(flags), choice)
  }
})

test('savedElsewherePath keeps the sub-folders under the chosen folder', () => {
  assert.equal(savedElsewherePath('/data/optimised/tv/', 'Show/Season 02/Episode.mkv'), '/data/optimised/tv/Show/Season 02/Episode.mkv')
  assert.equal(savedElsewherePath('/data/optimised', 'Show\\Episode.mkv'), '/data/optimised/Show/Episode.mkv')
})
