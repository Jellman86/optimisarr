import type { PlaybackHold } from './api.ts'
import type { Messages } from './i18n/en.ts'

// The phrases a playback line is built from, taken from the active locale so the order and
// wording can change per language. Kept as plain data so this module stays pure and testable.
export type PlaybackPhrases = {
  episodeNumber: string
  withYear: string
  track: string
  unknown: string
  userOnDevice: string
  onDevice: string
  paused: string
  more: string
}

export const playbackPhrases = (m: Messages): PlaybackPhrases => ({
  episodeNumber: m.queue.playback_episode_number,
  withYear: m.queue.playback_with_year,
  track: m.queue.playback_track,
  unknown: m.queue.playback_unknown,
  userOnDevice: m.queue.playback_user_on_device,
  onDevice: m.queue.playback_on_device,
  paused: m.queue.playback_paused,
  more: m.queue.playback_more,
})

const fill = (template: string, params: Record<string, string | number>) =>
  template.replace(/\{(\w+)\}/g, (_, key: string) => (key in params ? String(params[key]) : `{${key}}`))

/** What is playing: "Show · S2E5 · Title", "Film (1999)" or "Song by Artist". */
export function playbackTitle(hold: PlaybackHold, phrases: PlaybackPhrases): string {
  if (hold.kind === 'Episode') {
    const number = hold.season != null && hold.episode != null
      ? fill(phrases.episodeNumber, { season: hold.season, episode: hold.episode })
      : null
    const parts = [hold.series, number, hold.title].filter((part): part is string => !!part)
    if (parts.length > 0) return parts.join(' · ')
  }
  if (hold.kind === 'Track' && hold.title && hold.artist) return fill(phrases.track, { title: hold.title, artist: hold.artist })
  if (hold.title && hold.year != null && hold.kind === 'Movie') return fill(phrases.withYear, { title: hold.title, year: hold.year })
  return hold.title ?? phrases.unknown
}

/** Who is watching and where, or null when the server did not say or the watcher hides viewers. */
export function playbackViewer(hold: PlaybackHold, phrases: PlaybackPhrases): string | null {
  if (hold.user && hold.device) return fill(phrases.userOnDevice, { user: hold.user, device: hold.device })
  if (hold.user) return hold.user
  if (hold.device) return fill(phrases.onDevice, { device: hold.device })
  return null
}

/** One full line: "Show · S2E5 · Title — alex on Living Room TV (paused)". */
export function playbackLine(hold: PlaybackHold, phrases: PlaybackPhrases): string {
  const viewer = playbackViewer(hold, phrases)
  return [playbackTitle(hold, phrases) + (viewer ? ` — ${viewer}` : ''), hold.paused ? phrases.paused : null]
    .filter(Boolean).join(' ')
}

/** A compact form for narrow places: the first playback and who, then how many more. */
export function playbackSummary(holds: readonly PlaybackHold[], phrases: PlaybackPhrases): string | null {
  const [first] = holds
  if (!first) return null
  const head = playbackTitle(first, phrases) + (first.user ? ` — ${first.user}` : '')
  return holds.length > 1 ? `${head} ${fill(phrases.more, { count: holds.length - 1 })}` : head
}
