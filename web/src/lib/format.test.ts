import assert from 'node:assert/strict'
import test from 'node:test'
import { formatRelative, mediaTitle, savedPercent } from './format.ts'

const now = new Date('2026-09-25T07:00:00Z')

test('recent times read as minutes, hours or days ago', () => {
  assert.equal(formatRelative('2026-09-25T06:55:00Z', now, 'en'), '5 minutes ago')
  assert.equal(formatRelative('2026-09-25T04:02:48Z', now, 'en'), '3 hours ago')
  assert.equal(formatRelative('2026-09-23T07:00:00Z', now, 'en'), '2 days ago')
  assert.equal(formatRelative('2026-09-25T06:59:50Z', now, 'en'), 'now')
})

test('saved percent is rounded and never claims a saving the file did not make', () => {
  assert.equal(savedPercent(1_282_683_553, 368_922_428), 71)
  assert.equal(savedPercent(1000, 1200), 0)
  assert.equal(savedPercent(0, 0), 0)
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
