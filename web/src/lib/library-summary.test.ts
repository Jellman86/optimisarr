import assert from 'node:assert/strict'
import test from 'node:test'
import type { RuleProfileSpec, SaveLibrary } from './api.ts'
import { newLibraryDefaults } from './library-defaults.ts'
import { en } from './i18n/en.ts'
import { optionalChecks, presetChanges, summariseLibrary, vmafLevelName, type SummaryContext } from './library-summary.ts'

const balanced: RuleProfileSpec = {
  profile: 'ConservativeHevc', codec: 'hevc', container: 'mp4', crf: 24, hdrHandling: 'Exclude',
  videoAudioCodec: 'aac', videoAudioBitrateKbps: 160, audioTargetCodec: 'aac', audioBitrateKbps: 128, downmixToStereo: false,
}
const context: SummaryContext = { m: en, locale: 'en', preset: balanced, retentionDays: 30, dryRun: false }

function library(changes: Partial<SaveLibrary> = {}): SaveLibrary {
  return { ...newLibraryDefaults(), name: 'TV', path: '/data/media/tv', mediaType: 'TV', ...changes }
}

function line(topic: string, changes: Partial<SaveLibrary> = {}, extra: Partial<SummaryContext> = {}) {
  const found = summariseLibrary(library(changes), { ...context, ...extra }).find((candidate) => candidate.topic === topic)
  assert.ok(found, `no ${topic} line`)
  return found
}

const tagTexts = (topic: string, changes: Partial<SaveLibrary> = {}, extra: Partial<SummaryContext> = {}) =>
  line(topic, changes, extra).tags.map((tag) => `${tag.tone}:${tag.text}`)

test('a new TV library reads as five lines with nothing left out', () => {
  const lines = summariseLibrary(library(), context)

  assert.deepEqual(lines.map((entry) => entry.topic), ['files', 'video', 'audio', 'checks', 'schedule'])
  assert.equal(line('files').text, 'All video files. Skips files already in HEVC (H.265), files that are already small for their picture, HDR files, Dolby Vision files and interlaced video.')
  assert.equal(line('video').text, 'Re-encodes video to HEVC (H.265). Each file gets the smallest size that still scores 93 (Visually lossless) against the original. Picture size, frame rate and colour are kept. Each file keeps its container and its name.')
  assert.equal(line('video').detail, 'Balanced preset')
  assert.equal(line('audio').text, 'Converts audio to AAC. Keeps surround. Keeps every audio language. Keeps every subtitle.')
  assert.equal(line('checks').text, 'A new file must play from start to end, be the same length, keep every audio track, be smaller and score 93 for picture quality.')
  assert.equal(line('schedule').text, 'You queue files yourself. A file that passes waits for you, then replaces the original. Originals stay in Quarantine for 30 days, then they are deleted.')
})

test('the files line names every skip rule that is on', () => {
  const text = line('files', {
    minFileSizeBytes: 200 * 1024 * 1024, reencodeSameCodecAboveBytes: 1024 ** 3, skipEfficientSources: false,
    maxHeight: 1440, excludePaths: 'Extras\nSamples', excludeHardLinkedFiles: true, skipSourceCodecs: 'vp9, av1', enabled: false,
  }).text

  assert.match(text, /^Only video files over 200 MB\./)
  assert.match(text, /files already in HEVC \(H\.265\) under 1(\.0+)? GB/)
  assert.doesNotMatch(text, /already small for their picture/)
  assert.match(text, /video taller than 1440p/)
  assert.match(text, /anything with Extras and Samples in its path/)
  assert.match(text, /hardlinked files/)
  assert.match(text, /vp9 and av1 files/)
  assert.match(text, /This library is switched off, so nothing is scanned or queued\.$/)
})

test('counts are shown when the saved rules have been resolved', () => {
  assert.equal(line('files').detail, null)
  assert.equal(line('files', {}, { counts: { eligible: 1283, skipped: 863 } }).detail, '1,283 picked · 863 skipped, each with its reason in Candidates')
})

test('every setting that changes the picture adds its own sentence and one tag that cannot be dropped', () => {
  for (const [changes, sentence] of [
    [{ videoDownscaleHeight: 1080 }, 'Shrinks video taller than 1080p down to 1080p.'],
    [{ maxFrameRate: 30 }, 'Halves frame rates above 30 fps.'],
    [{ cropBlackBars: true }, 'Removes black bars.'],
    [{ hdrHandling: 'TonemapToSdr' }, 'Converts HDR to SDR.'],
    [{ optimiseDolbyVision: true }, 'Re-encodes Dolby Vision files, which loses Dolby Vision.'],
    [{ deinterlaceVideo: true }, 'Deinterlaces interlaced video.'],
    [{ noiseReduction: 'Strong' }, 'Reduces noise (Strong).'],
    [{ superResolutionEnabled: true }, 'Upscales with AI before the checks.'],
  ] as [Partial<SaveLibrary>, string][]) {
    const found = line('video', changes)
    assert.ok(found.text.includes(sentence), `${sentence} missing from: ${found.text}`)
    assert.ok(!found.text.includes('Picture size, frame rate and colour are kept.'))
    assert.deepEqual(found.tags.filter((tag) => tag.tone === 'warn').map((tag) => tag.text), ['Changes the picture'])
  }
})

test('a fixed container says files are renamed, and keeping the source container says names stay', () => {
  assert.deepEqual(tagTexts('video'), ['neutral:File names stay the same'])
  assert.deepEqual(tagTexts('video', { targetContainer: null }), ['warn:Renames files'])
  assert.match(line('video', { targetContainer: 'mkv' }).text, /Files are written as MKV\. A file in another container gets a new name\./)
})

test('the quality sentence follows the strategy and whether the picture is checked', () => {
  assert.match(line('video', { videoQualityStrategy: 'Fixed' }).text, /Every file uses quality 24, then must score 93 \(Visually lossless\) against the original\./)
  assert.match(line('video', { videoQualityStrategy: 'Fixed', vmafQualityGateEnabled: false, qualityCrf: 21 }).text, /Every file uses quality 21\. The picture is not scored against the original\./)
  assert.match(line('video', { minVmafHarmonicMean: 91 }).text, /still scores 91 against the original\./)
  assert.equal(vmafLevelName(library({ minVmafHarmonicMean: 85, minVmafMin: 70, minVmafCatastrophicMin: 40 }), en), 'Balanced')
})

test('no-encode modes say nothing is re-encoded and never claim a quality target', () => {
  const remux = line('video', { ruleProfile: 'RemuxCleanup' })
  const cleanup = line('video', { ruleProfile: 'TrackCleanup' })

  assert.match(remux.text, /^Cleans up each file's container without re-encoding\./)
  assert.doesNotMatch(remux.text, /scores/)
  assert.match(cleanup.text, /No languages are set to keep yet, so every file is skipped\.$/)
  assert.doesNotMatch(line('video', { ruleProfile: 'TrackCleanup', keepAudioLanguages: 'eng' }).text, /skipped/)
  assert.doesNotMatch(line('checks', { ruleProfile: 'RemuxCleanup' }).text, /picture quality/)
  assert.doesNotMatch(line('files', { ruleProfile: 'RemuxCleanup' }).text, /HDR/)
})

test('keeping some languages says tracks are removed and tags the line', () => {
  const found = line('audio', { keepAudioLanguages: 'eng, jpn', keepSubtitleLanguages: 'eng' })

  assert.match(found.text, /Keeps ENG and JPN audio only\. Tracks tagged with another language are removed\./)
  assert.match(found.text, /Keeps ENG subtitles only\. If none match, the new file has no subtitles\./)
  assert.deepEqual(found.tags, [{ tone: 'warn', text: 'Removes tracks' }])
  assert.deepEqual(line('audio').tags, [])
})

test('copied audio and a stereo mix-down are said plainly', () => {
  assert.match(line('audio', { videoAudioCodec: 'copy' }).text, /^Audio is left untouched\./)
  assert.match(line('audio', { downmixToStereo: true }).text, /Mixes surround down to stereo\./)
})

test('music and photo libraries get their own lines and no video line', () => {
  const music = summariseLibrary(library({ mediaType: 'Music', audioTargetCodec: 'opus', audioBitrateKbps: 96 }), context)
  const photo = summariseLibrary(library({ mediaType: 'Photo', targetImageFormat: 'webp', imageDownscaleMode: 'MaxLongEdge', imageDownscaleValue: 3840 }), context)

  assert.deepEqual(music.map((entry) => entry.topic), ['files', 'audio', 'checks', 'schedule'])
  assert.equal(music[1].title, 'Audio')
  assert.equal(music[1].text, 'Converts lossless audio to Opus at 96 kb/s. Lossy audio is left alone.')
  assert.deepEqual(photo.map((entry) => entry.topic), ['files', 'images', 'checks', 'schedule'])
  assert.equal(photo[1].text, 'Converts lossless images to WebP at quality 80. Images that are already compressed are left alone. Shrinks images to fit 3840 px on the long edge.')
  assert.deepEqual(photo[1].tags, [{ tone: 'warn', text: 'Changes the picture' }])
  assert.match(photo[2].text, /^A new file must open without errors, be smaller, match the original picture closely and keep its EXIF data and colour profile\.$/)
})

test('the schedule line follows the choice of three, the window, retention and dry-run', () => {
  assert.match(line('schedule', { autoEnqueueEnabled: true }).text, /^Eligible files are queued automatically, at any time\./)
  assert.match(line('schedule', { autoEnqueueEnabled: true, autoEnqueueWindowStart: '01:00', autoEnqueueWindowEnd: '06:00' }).text, /between 01:00 and 06:00\./)
  assert.match(line('schedule', { autoReplace: true }).text, /A file that passes replaces the original straight away\./)
  assert.deepEqual(tagTexts('schedule'), ['warn:Deletes originals after 30 days'])
  assert.deepEqual(tagTexts('schedule', {}, { retentionDays: 1 }), ['warn:Deletes originals after 1 day'])
  assert.match(line('schedule', {}, { retentionDays: 0 }).text, /Originals stay in Quarantine until you approve them\.$/)
  assert.deepEqual(tagTexts('schedule', {}, { retentionDays: 0 }), [])
  assert.match(line('schedule', {}, { dryRun: true }).text, /Replacing is locked for now \(dry-run\)\.$/)
  assert.match(line('schedule', { priority: 2 }).text, /Queue priority: Highest\.$/)
})

test('saving elsewhere never mentions Quarantine or deleting, even with replace automatically left on', () => {
  const found = line('schedule', { moveOnComplete: true, targetFolder: '/data/optimised/tv', autoReplace: true }, { dryRun: true })

  assert.equal(found.text, 'You queue files yourself. A file that passes is saved under /data/optimised/tv. The original is not touched.')
  assert.deepEqual(found.tags, [])
})

test('presetChanges counts only what differs from the preset, and same-as-source is not a change', () => {
  assert.deepEqual(presetChanges(library(), balanced), [])
  assert.deepEqual(presetChanges(library({ targetVideoCodec: 'hevc', qualityCrf: 24, hdrHandling: 'Exclude' }), balanced), [])
  assert.deepEqual(presetChanges(library({ targetVideoCodec: 'av1', targetContainer: 'mkv', qualityCrf: 20, downmixToStereo: true }), balanced),
    ['targetVideoCodec', 'targetContainer', 'qualityCrf', 'downmixToStereo'])
  assert.equal(line('video', { qualityCrf: 20 }).detail, 'Balanced preset · 1 change')
  assert.equal(line('video', { qualityCrf: 20, targetVideoCodec: 'av1' }).detail, 'Balanced preset · 2 changes')
})

test('optionalChecks lists only the checks this kind of library can have', () => {
  assert.deepEqual(optionalChecks(library()).map((check) => check.key), ['audio', 'subtitles', 'smaller', 'picture', 'soundtrack', 'loudness', 'clipping'])
  assert.deepEqual(optionalChecks(library({ mediaType: 'Photo' })).map((check) => check.key), ['smaller', 'ssim', 'imageScore', 'metadata'])
  assert.equal(line('checks').detail, '3 optional on · 4 optional off')
})
