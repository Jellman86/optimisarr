export function formatSize(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(value < 10 && unit > 0 ? 1 : 0)} ${units[unit]}`
}

export function formatDuration(seconds: number | null): string {
  if (seconds === null) return '—'
  const total = Math.round(seconds)
  const hours = Math.floor(total / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  return hours > 0 ? `${hours}h ${minutes}m` : `${minutes}m`
}

// "5 minutes ago", "3 hours ago", "2 days ago" in the reader's language. Rounded rather than
// floored: something that finished 2 h 57 min ago is "3 hours ago" to a person.
export function formatRelative(value: string | Date, now: Date = new Date(), locale?: string): string {
  const seconds = (new Date(value).getTime() - now.getTime()) / 1000
  const format = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' })
  const elapsed = Math.abs(seconds)
  if (elapsed < 45) return format.format(0, 'second')
  if (elapsed < 45 * 60) return format.format(Math.round(seconds / 60), 'minute')
  if (elapsed < 22 * 3600) return format.format(Math.round(seconds / 3600), 'hour')
  return format.format(Math.round(seconds / 86400), 'day')
}

export type SizeChangeStatus = 'saved' | 'unchanged' | 'grew'

// The direction of a size change is a fact about the bytes, and the bytes are its only source:
// a smaller output saved, a larger one grew, an equal one did not change. It is never read from
// a rounded percentage, because a real change smaller than the rounding — 1000 → 999 is a 0.1%
// saving — would be reported as no change at all. Sizes that cannot be compared — non-finite, or
// an empty or missing file on either side — leave null rather than a guess. An output of zero
// bytes is never a real result: the server reports 0 when it could not size the output at all.
export function sizeChangeStatus(sourceBytes: number, outputBytes: number): SizeChangeStatus | null {
  if (![sourceBytes, outputBytes].every(Number.isFinite)) return null
  if (sourceBytes <= 0 || outputBytes <= 0) return null
  if (outputBytes < sourceBytes) return 'saved'
  if (outputBytes > sourceBytes) return 'grew'
  return 'unchanged'
}

// The change in file size as a percentage, signed the way the bytes say: negative when the
// output came out smaller, positive when it grew. Changes of a percent or more read as whole
// numbers ("−71%"); smaller changes keep a tenth of a percent so they still read as the change
// they are ("−0.1%", "+0.4%") and never as a quiet 0%. A real change finer than that tenth —
// one byte in a few megabytes — is shown at the display's resolution, ±0.1%, rather than as
// no change, so the figure always agrees with the direction the bytes carry. For the same
// reason a near-total saving stops at −99.9%: the output still exists, so it is not −100%.
export function sizeChangePercent(sourceBytes: number, outputBytes: number): number | null {
  const status = sizeChangeStatus(sourceBytes, outputBytes)
  if (status === null) return null
  const raw = (outputBytes / sourceBytes - 1) * 100
  // Close to a total saving, a whole number would round up to −100%, so keep the tenth there.
  if (raw < -99.5) return Math.max(-99.9, Math.round(raw * 10) / 10)
  const percent = Math.abs(raw) >= 1 ? Math.round(raw) : Math.round(raw * 10) / 10
  // Math.round(-0.01) is -0, and a -0 would slip past every `< 0` check as a saving.
  if (percent === 0 && status !== 'unchanged') return status === 'saved' ? -0.1 : 0.1
  return percent
}

// Every panel reads a size change from this one place, so the sign and the zero are written
// once: smaller reads "−71%", larger reads "+20%", a tenth of a percent keeps its decimal
// ("−0.1%"), no change reads "0%", unknown reads "—". The minus is U+2212, the sign the
// Quarantine list, the preview badge and the translated copy already draw a saving with, and
// the sign that pairs with the plain plus above the number.
export function formatSizeChangePercent(percent: number | null): string {
  if (percent === null) return '—'
  if (percent === 0) return '0%'
  const magnitude = Number.isInteger(percent) ? Math.abs(percent).toString() : Math.abs(percent).toFixed(1)
  return percent < 0 ? `−${magnitude}%` : `+${magnitude}%`
}

// The change in file size itself, signed the same way as the percent: "−400 MB", "+200 MB",
// "0 MB", and "—" when the change cannot be judged. The direction comes from the bytes, so a
// size figure and its percent beside it can never disagree about which way the file went.
export function formatSizeChangeBytes(sourceBytes: number, outputBytes: number): string {
  const status = sizeChangeStatus(sourceBytes, outputBytes)
  if (status === null) return '—'
  if (status === 'unchanged') return formatSize(0)
  return `${status === 'saved' ? '−' : '+'}${formatSize(Math.abs(outputBytes - sourceBytes))}`
}

export interface SizeBarShares {
  /** The source file's share of the comparison bar, in percent. */
  source: number
  /** The output file's share of the comparison bar, in percent. */
  output: number
}

// The size comparison bar is drawn against whichever of the two files is the larger one, so a
// file that grew shows the part that overflows the source rather than a full bar that reads as
// a saving. Sizes that cannot be judged share out nothing at all.
export function sizeBarShares(sourceBytes: number, outputBytes: number): SizeBarShares | null {
  if (sizeChangeStatus(sourceBytes, outputBytes) === null) return null
  const scale = Math.max(sourceBytes, outputBytes)
  return { source: (sourceBytes / scale) * 100, output: (outputBytes / scale) * 100 }
}

export interface MediaTitle {
  primary: string | null
  episode: string | null
  secondary: string | null
}

const RELEASE_TAGS = /\s+(?:WEBDL|WEB-DL|WEBRip|Bluray|BluRay|HDTV|SDTV|DVD|Remux)\b.*$/i

// "Show - S01E02 - Episode title WEBDL-1080p.mkv" reads as the show, the episode and its title; a
// film keeps its name and year. The release tags describe the source copy, not the programme.
export function mediaTitle(path: string | null): MediaTitle {
  if (!path) return { primary: null, episode: null, secondary: null }
  const base = (path.replace(/\\/g, '/').split('/').pop() ?? path).replace(/\.[^.]+$/, '')
  const episode = base.match(/^(.*?) - (S\d+E\d+(?:-?E\d+)?) - (.*)$/i)
  if (episode) return { primary: episode[1].trim(), episode: episode[2].toUpperCase(), secondary: episode[3].replace(RELEASE_TAGS, '').trim() || null }
  const film = base.match(/^(.*?\(\d{4}\))/)
  if (film) return { primary: film[1].trim(), episode: null, secondary: null }
  return { primary: base.replace(RELEASE_TAGS, '').replace(/[._]+/g, ' ').trim() || base, episode: null, secondary: null }
}

const CONTAINER_NAMES: Record<string, string> = {
  matroska: 'MKV', webm: 'WebM', mpegts: 'MPEG-TS', avi: 'AVI', asf: 'WMV', flv: 'FLV',
  mp3: 'MP3', flac: 'FLAC', ogg: 'Ogg', wav: 'WAV', aiff: 'AIFF', image2: 'Image',
}

// ffprobe names a demuxer, not a file type: MP4 and MOV share "mov,mp4,m4a,3gp,3g2,mj2" and MKV
// and WebM share "matroska,webm". The file's own extension says which one it is.
export function containerLabel(container: string | null, path: string | null): string | null {
  if (!container) return null
  const extension = path?.match(/\.([a-z0-9]{2,4})$/i)?.[1]
  const names = container.split(',').map((name) => name.trim().toLowerCase())
  if (extension && (names.length > 1 || names.includes(extension.toLowerCase()))) {
    return extension.toLowerCase() === 'webm' ? 'WebM' : extension.toUpperCase()
  }
  return CONTAINER_NAMES[names[0]] ?? names[0].toUpperCase()
}

// "und" is ISO 639's code for an untagged track; a person reads it as a typo.
export function languageList(languages: string, unknown: string): string {
  return languages
    .split(',')
    .map((language) => language.trim())
    .filter(Boolean)
    .map((language) => (language.toLowerCase() === 'und' ? unknown : language))
    .join(', ')
}
