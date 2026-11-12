import assert from 'node:assert/strict'
import test from 'node:test'
import { containerLabel, formatSizeChangeBytes, formatSizeChangePercent, formatRelative, languageList, mediaTitle, sizeBarShares, sizeChangePercent, sizeChangeStatus } from './format.ts'

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
  // A change smaller than one percent keeps its decimal: 0.1% is a change, not a rounding
  // boundary that reads as no change at all.
  assert.equal(sizeChangePercent(1000, 999), -0.1)
  assert.equal(sizeChangePercent(1000, 1001), 0.1)
  assert.equal(sizeChangePercent(1000, 1004), 0.4)
  // Rounding either way: -33.4% reads as -33, +15.5% reads as 16.
  assert.equal(sizeChangePercent(1000, 666), -33)
  assert.equal(sizeChangePercent(1000, 1155), 16)
  // A file that grew.
  assert.equal(sizeChangePercent(1000, 1200), 20)
  assert.equal(sizeChangePercent(1000, 1500), 50)
  // A near-total saving is not the whole file: the output still exists.
  assert.equal(sizeChangePercent(1_000_000_000, 1_000_000), -99.9)
  // Near the end a tenth is kept, so 99.6% is not rounded up to the whole file.
  assert.equal(sizeChangePercent(1000, 4), -99.6)
  assert.equal(sizeChangePercent(1000, 6), -99)
  // An output of no bytes means it could not be sized, not that everything was saved.
  assert.equal(sizeChangePercent(1000, 0), null)
  // A real pair of sizes.
  assert.equal(sizeChangePercent(1_282_683_553, 368_922_428), -71)
})

test('a change finer than a tenth of a percent still reads as the change it is', () => {
  // One byte in ten thousand is 0.01%, finer than the display's resolution. It is shown at
  // that resolution, signed by the bytes, rather than as a 0% that would describe a changed
  // file as unchanged.
  assert.equal(sizeChangePercent(9999, 9998), -0.1)
  assert.equal(sizeChangePercent(9998, 9999), 0.1)
  assert.ok(!Object.is(sizeChangePercent(9999, 9998), -0))
})

test('a size change that cannot be judged is unknown, never a figure', () => {
  assert.equal(sizeChangePercent(0, 1000), null)
  assert.equal(sizeChangePercent(-1, 1000), null)
  assert.equal(sizeChangePercent(1000, -1), null)
  assert.equal(sizeChangePercent(Number.NaN, 1000), null)
  assert.equal(sizeChangePercent(1000, Number.POSITIVE_INFINITY), null)
  assert.equal(sizeChangePercent(0, 0), null)
})

test('the direction is read from the bytes, and unknown is not no-change', () => {
  assert.equal(sizeChangeStatus(1000, 600), 'saved')
  assert.equal(sizeChangeStatus(1000, 1000), 'unchanged')
  assert.equal(sizeChangeStatus(1000, 1200), 'grew')
  // A change below a percent is still the direction it is, whatever a percent rounds to.
  assert.equal(sizeChangeStatus(1000, 999), 'saved')
  assert.equal(sizeChangeStatus(1000, 1004), 'grew')
  assert.equal(sizeChangeStatus(0, 1000), null)
  assert.equal(sizeChangeStatus(1000, -1), null)
  assert.equal(sizeChangeStatus(Number.NaN, 1000), null)
  assert.equal(sizeChangeStatus(0, 0), null)
})

test('the signed percent always agrees with the direction the bytes carry', () => {
  const pairs = [[1000, 1], [9999, 9998], [1000, 999], [1000, 999.5], [1000, 1000],
    [1000, 1000.5], [1000, 1001], [1000, 1004], [1000, 1200], [1000, 1500], [600, 1000]] as const
  for (const [source, output] of pairs) {
    const percent = sizeChangePercent(source, output)
    const status = sizeChangeStatus(source, output)
    assert.ok(percent !== null && status !== null, `${source} → ${output}`)
    if (status === 'saved') assert.ok(percent < 0, `${source} → ${output}: ${percent}`)
    if (status === 'grew') assert.ok(percent > 0, `${source} → ${output}: ${percent}`)
    if (status === 'unchanged') assert.equal(percent, 0)
  }
})

test('size change text carries its sign once, from the one formatter', () => {
  assert.equal(formatSizeChangePercent(-40), '−40%')
  assert.equal(formatSizeChangePercent(20), '+20%')
  assert.equal(formatSizeChangePercent(0), '0%')
  // A change below a percent keeps its decimal.
  assert.equal(formatSizeChangePercent(-0.1), '−0.1%')
  assert.equal(formatSizeChangePercent(0.4), '+0.4%')
  assert.equal(formatSizeChangePercent(null), '—')
})

test('the size-change minus is the sign the app draws elsewhere, not an ASCII hyphen', () => {
  // U+2212 MINUS SIGN, the sign the Quarantine list, the preview badge and the i18n copy
  // already use. An ASCII hyphen here would read as a different sign in a different place.
  assert.equal(formatSizeChangePercent(-40), '−40%')
  assert.equal(formatSizeChangePercent(-0.1), '−0.1%')
  assert.equal(formatSizeChangePercent(-40).includes('-'), false)
  assert.equal(formatSizeChangePercent(-40).codePointAt(0), 0x2212)
})

test('the size change in bytes is signed like the percent, from the same direction', () => {
  assert.equal(formatSizeChangeBytes(1000, 600), '−400 B')
  assert.equal(formatSizeChangeBytes(600, 1000), '+400 B')
  assert.equal(formatSizeChangeBytes(1000, 1000), '0 B')
  assert.equal(formatSizeChangeBytes(1000, 0), '—')
  assert.equal(formatSizeChangeBytes(0, 1000), '—')
  assert.equal(formatSizeChangeBytes(Number.NaN, 1000), '—')
  // One gigabyte to six hundred megabytes.
  assert.equal(formatSizeChangeBytes(1_000_000_000, 600_000_000), '−381 MB')
  assert.equal(formatSizeChangeBytes(600_000_000, 1_000_000_000), '+381 MB')
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
  assert.equal(sizeBarShares(1000, 0), null)
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
