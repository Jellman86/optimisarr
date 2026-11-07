import assert from 'node:assert/strict'
import test from 'node:test'
import { containerLabel, formatSizeChangePercent, formatRelative, languageList, mediaTitle, sizeBarShares, sizeChangePercent, sizeChangeStatus } from './format.ts'

const now = new Date('2026-09-25T07:00:00Z')

test('recent times read as minutes, hours or days ago', () => {
  assert.equal(formatRelative('2026-09-25T06:55:00Z', now, 'en'), '5 minutes ago')
  assert.equal(formatRelative('2026-09-25T04:02:48Z', now, 'en'), '3 hours ago')
  assert.equal(formatRelative('2026-09-23T07:00:00Z', now, 'en'), '2 days ago')
  assert.equal(formatRelative('2026-09-25T06:59:50Z', now, 'en'), 'now')
})

test('size change follows the size itself: negative when the output is smaller, positive when it grew', () => {
  // A saving of 40%.
  assert.equal(sizeChangePercent(1000, 600), -40)
  // The same size.
  assert.equal(sizeChangePercent(1000, 1000), 0)
  // A rounding boundary: 0.1% smaller rounds to no change at all.
  assert.equal(sizeChangePercent(1000, 999), 0)
  // Rounding either way: -33.4% reads as -33, +15.5% reads as 16.
  assert.equal(sizeChangePercent(1000, 666), -33)
  assert.equal(sizeChangePercent(1000, 1155), 16)
  // A file that grew.
  assert.equal(sizeChangePercent(1000, 1200), 20)
  assert.equal(sizeChangePercent(1000, 1500), 50)
  // The whole file went.
  assert.equal(sizeChangePercent(1000, 0), -100)
  // A real pair of sizes.
  assert.equal(sizeChangePercent(1_282_683_553, 368_922_428), -71)
})

test('a size change that cannot be judged is unknown, never a figure', () => {
  assert.equal(sizeChangePercent(0, 1000), null)
  assert.equal(sizeChangePercent(-1, 1000), null)
  assert.equal(sizeChangePercent(1000, -1), null)
  assert.equal(sizeChangePercent(Number.NaN, 1000), null)
  assert.equal(sizeChangePercent(1000, Number.POSITIVE_INFINITY), null)
  assert.equal(sizeChangePercent(0, 0), null)
})

test('a size change of nothing is never a negative zero', () => {
  // Math.round(-0.1) is -0, and a -0 would slip past every `< 0` check as a saving.
  assert.ok(!Object.is(sizeChangePercent(1000, 999), -0))
  assert.ok(!Object.is(sizeChangePercent(1000, 1001), -0))
  assert.equal(sizeChangePercent(1000, 1001), 0)
})

test('size change reads as saved, grew or unchanged, and unknown is not no-change', () => {
  assert.equal(sizeChangeStatus(-40), 'saved')
  assert.equal(sizeChangeStatus(20), 'grew')
  assert.equal(sizeChangeStatus(0), 'unchanged')
  assert.equal(sizeChangeStatus(-0), 'unchanged')
  assert.equal(sizeChangeStatus(null), null)
})

test('size change text carries its sign once, from the one formatter', () => {
  assert.equal(formatSizeChangePercent(-40), '−40%')
  assert.equal(formatSizeChangePercent(20), '+20%')
  assert.equal(formatSizeChangePercent(0), '0%')
  assert.equal(formatSizeChangePercent(null), '—')
})

test('the size-change minus is the sign the app draws elsewhere, not an ASCII hyphen', () => {
  // U+2212 MINUS SIGN, the sign the Quarantine list, the preview badge and the i18n copy
  // already use. An ASCII hyphen here would read as a different sign in a different place.
  assert.equal(formatSizeChangePercent(-40), '−40%')
  assert.equal(formatSizeChangePercent(-40).includes('-'), false)
  assert.equal(formatSizeChangePercent(-40).codePointAt(0), 0x2212)
})

test('the comparison bar is measured against the larger file, so growth cannot hide as a full bar', () => {
  const smaller = sizeBarShares(1000, 600)
  assert.equal(smaller?.source, 100)
  assert.equal(smaller?.output, 60)
  const same = sizeBarShares(1000, 1000)
  assert.equal(same?.source, 100)
  assert.equal(same?.output, 100)
  const bigger = sizeBarShares(1000, 1500)
  assert.ok(Math.abs((bigger?.source ?? 0) - 66.6667) < 0.001)
  assert.equal(bigger?.output, 100)
  const empty = sizeBarShares(1000, 0)
  assert.equal(empty?.source, 100)
  assert.equal(empty?.output, 0)
})

test('the comparison bar shares out nothing for sizes that cannot be judged', () => {
  assert.equal(sizeBarShares(0, 1000), null)
  assert.equal(sizeBarShares(1000, -1), null)
  assert.equal(sizeBarShares(Number.NaN, 1000), null)
})

test('episode files read as show, episode and title without the release tags', () => {
  assert.deepEqual(mediaTitle('adult/American Horror Story/Season 13/American Horror Story - S13E02 - 1-13 AM WEBDL-1080p.mkv'),
    { primary: 'American Horror Story', episode: 'S13E02', secondary: '1-13 AM' })
  assert.deepEqual(mediaTitle('kids/Pokémon/Season 1/Pokémon - S01E03 - Ash Catches a Pokémon HDTV-720p.mkv'),
    { primary: 'Pokémon', episode: 'S01E03', secondary: 'Ash Catches a Pokémon' })
})

test('films read as their name and year, and anything else as its file name', () => {
  assert.deepEqual(mediaTitle('Blackfish (2013)/Blackfish (2013) Bluray-1080p.mp4'), { primary: 'Blackfish (2013)', episode: null, secondary: null })
  assert.deepEqual(mediaTitle('odd/home_video.final.mkv'), { primary: 'home video final', episode: null, secondary: null })
  assert.deepEqual(mediaTitle(null), { primary: null, episode: null, secondary: null })
})

test('containers read as the file type a person knows, not the ffprobe demuxer list', () => {
  assert.equal(containerLabel('mov,mp4,m4a,3gp,3g2,mj2', 'Films/Blackfish (2013).mp4'), 'MP4')
  assert.equal(containerLabel('mov,mp4,m4a,3gp,3g2,mj2', 'clip.mov'), 'MOV')
  assert.equal(containerLabel('matroska,webm', 'Show - S01E01.mkv'), 'MKV')
  assert.equal(containerLabel('matroska,webm', 'clip.webm'), 'WebM')
  assert.equal(containerLabel('mpegts', 'recording.ts'), 'MPEG-TS')
  assert.equal(containerLabel('flac', null), 'FLAC')
  assert.equal(containerLabel(null, 'a.mkv'), null)
})

test('untagged track languages read as unknown', () => {
  assert.equal(languageList('und', 'unknown language'), 'unknown language')
  assert.equal(languageList('eng, und,jpn', 'unknown language'), 'eng, unknown language, jpn')
})
