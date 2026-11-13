import assert from 'node:assert/strict'
import test from 'node:test'
import { holdsVideo, newLibraryDefaults, withMediaType } from './library-defaults.ts'

test('a new library starts by choosing quality from a picture score, which only video can have', () => {
  const defaults = newLibraryDefaults()

  assert.equal(defaults.videoQualityStrategy, 'AdaptiveVmaf')
  assert.equal(defaults.vmafQualityGateEnabled, true)
})

for (const kind of ['Music', 'Photo']) {
  test(`a ${kind.toLowerCase()} library drops everything that needs a picture score, which the server would refuse`, () => {
    const library = withMediaType({ ...newLibraryDefaults(), name: 'Shelf', path: '/media/shelf' }, kind)

    assert.equal(holdsVideo(kind), false)
    assert.equal(library.mediaType, kind)
    assert.equal(library.videoQualityStrategy, 'Fixed')
    assert.equal(library.vmafQualityGateEnabled, false)
    assert.equal(library.minVmafHarmonicMean, null)
    assert.equal(library.minVmafMin, null)
    assert.equal(library.minVmafCatastrophicMin, null)
    assert.equal(library.clipVmafEnabled, null)
    assert.equal(library.vmafFrameSubsample, null)
    assert.equal(library.name, 'Shelf')
  })
}

test('a video-only clean-up mode is replaced when the library stops holding video', () => {
  assert.equal(withMediaType({ ...newLibraryDefaults(), ruleProfile: 'RemuxCleanup' }, 'Music').ruleProfile, 'ConservativeHevc')
  assert.equal(withMediaType({ ...newLibraryDefaults(), ruleProfile: 'TrackCleanup' }, 'Photo').ruleProfile, 'ConservativeHevc')
  assert.equal(withMediaType({ ...newLibraryDefaults(), ruleProfile: 'ExperimentalAv1' }, 'Music').ruleProfile, 'ExperimentalAv1')
})

test('a library that holds video keeps its quality settings when its kind changes', () => {
  const tv = withMediaType({ ...newLibraryDefaults(), minVmafHarmonicMean: 91 }, 'TV')

  assert.equal(tv.mediaType, 'TV')
  assert.equal(tv.videoQualityStrategy, 'AdaptiveVmaf')
  assert.equal(tv.minVmafHarmonicMean, 91)
  assert.equal(holdsVideo('Other'), true)
})
