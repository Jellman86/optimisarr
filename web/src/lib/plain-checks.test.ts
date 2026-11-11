import assert from 'node:assert/strict'
import test from 'node:test'
import { en } from './i18n/en.ts'
import { isPictureQualityCheck, pictureQualityWasMeasured, plainCheckName } from './plain-checks.ts'

test('the checks a person meets most are said in plain words', () => {
  assert.equal(plainCheckName('Decode health', en.checkNames), 'Plays from start to end without errors')
  assert.equal(plainCheckName('Duration', en.checkNames), 'Same length')
  assert.equal(plainCheckName('Size saving', en.checkNames), 'Smaller than the original')
  assert.equal(plainCheckName('Audio languages', en.checkNames), 'The audio languages you asked to keep')
})

test('a check name with a suffix is still recognised', () => {
  assert.equal(plainCheckName('Perceptual quality (VMAF) — three samples', en.checkNames), 'Picture quality')
  assert.equal(plainCheckName('Audio loudness (EBU R128)', en.checkNames), 'Loudness kept')
  assert.equal(isPictureQualityCheck('Perceptual quality (VMAF)'), true)
  assert.equal(isPictureQualityCheck('Perceptual image quality (SSIMULACRA2)'), false)
  assert.equal(plainCheckName('Perceptual audio quality (Zimtohrli)', en.checkNames), 'Sound close to the original')
})

test('a check this file has not been taught keeps the name the server gave it', () => {
  assert.equal(plainCheckName('Some new gate', en.checkNames), 'Some new gate')
})

test('a picture-quality check that could not be measured is told apart from one that scored too low', () => {
  const name = 'Perceptual quality (VMAF)'

  assert.equal(pictureQualityWasMeasured({ name, detail: 'Harmonic mean 92.4; needed 93.' }), true)
  assert.equal(pictureQualityWasMeasured({ name, detail: 'Quality could not be measured: ffmpeg exited 1' }), false)
  assert.equal(pictureQualityWasMeasured({ name, detail: 'VMAF aggregates were missing from the measurement.' }), false)
  assert.equal(pictureQualityWasMeasured({ name: 'Duration', detail: 'Harmonic mean 92.4; needed 93.' }), false)
})
