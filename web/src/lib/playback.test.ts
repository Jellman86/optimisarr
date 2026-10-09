import { test } from 'node:test'
import assert from 'node:assert/strict'
import { playbackLine, playbackSummary, type PlaybackPhrases } from './playback.ts'
import type { PlaybackHold } from './api.ts'

const phrases: PlaybackPhrases = {
  episodeNumber: 'S{season}E{episode}',
  withYear: '{title} ({year})',
  track: '{title} by {artist}',
  unknown: 'Unknown title',
  userOnDevice: '{user} on {device}',
  onDevice: 'on {device}',
  paused: '(paused)',
  more: '+{count} more',
}

const hold = (overrides: Partial<PlaybackHold>): PlaybackHold => ({
  watcher: 'Plex', kind: 'Other', title: null, series: null, season: null, episode: null, year: null,
  artist: null, user: null, device: null, paused: false, ...overrides,
})

const episode = hold({ kind: 'Episode', title: 'Pilot', series: 'Example Show', season: 2, episode: 5, user: 'alex', device: 'Living Room TV' })

test('an episode names the show, its number and title, then who is watching and where', () => {
  assert.equal(playbackLine(episode, phrases), 'Example Show · S2E5 · Pilot · alex on Living Room TV')
})

test('a film carries its year, a track its artist, and a pause is said', () => {
  assert.equal(playbackLine(hold({ kind: 'Movie', title: 'Example Film', year: 1999, user: 'sam', paused: true }), phrases),
    'Example Film (1999) · sam (paused)')
  assert.equal(playbackLine(hold({ kind: 'Track', title: 'Example Song', artist: 'Example Artist', device: 'Kitchen' }), phrases),
    'Example Song by Example Artist · on Kitchen')
})

test('missing details are left out rather than shown blank, and a hidden viewer leaves the title alone', () => {
  assert.equal(playbackLine(hold({ kind: 'Episode', title: 'Pilot', series: 'Example Show' }), phrases), 'Example Show · Pilot')
  assert.equal(playbackLine(hold({}), phrases), 'Unknown title')
})

test('the compact summary names the first playback and counts the rest', () => {
  const film = hold({ kind: 'Movie', title: 'Example Film', user: 'sam' })
  assert.equal(playbackSummary([], phrases), null)
  assert.equal(playbackSummary([episode], phrases), 'Example Show · S2E5 · Pilot · alex')
  assert.equal(playbackSummary([episode, film], phrases), 'Example Show · S2E5 · Pilot · alex +1 more')
})
