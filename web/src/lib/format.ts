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

// The rounded change in file size: negative means the output came out smaller than the source
// (a saving), positive means it grew. The sign follows the size change itself, not the saving
// it produced, so a file that grew can never read as a saving. Sizes that cannot describe a
// change — non-finite, a missing or empty source, a negative output — leave null rather than a
// number a reader would have to guess at.
export function sizeChangePercent(sourceBytes: number, outputBytes: number): number | null {
  if (![sourceBytes, outputBytes].every(Number.isFinite)) return null
  if (sourceBytes <= 0 || outputBytes < 0) return null
  const percent = Math.round((outputBytes / sourceBytes - 1) * 100)
  // Math.round(-0.1) is -0, and a -0 would slip past every `< 0` check as a saving.
  return Object.is(percent, -0) ? 0 : percent
}

export type SizeChangeStatus = 'saved' | 'unchanged' | 'grew'

// The direction of a size change, for tone and wording. Unknown (null) is a state of its own;
// it is not "unchanged".
export function sizeChangeStatus(percent: number | null): SizeChangeStatus | null {
  if (percent === null) return null
  if (percent < 0) return 'saved'
  if (percent > 0) return 'grew'
  return 'unchanged'
}

// Every panel reads a size change from this one place, so the sign and the zero are written
// once: smaller reads "−71%", larger reads "+12%", no change reads "0%", unknown reads "—".
// The minus is U+2212, the sign the Quarantine list, the preview badge and the translated copy
// already draw a saving with, and the sign that pairs with the plain plus above the number.
export function formatSizeChangePercent(percent: number | null): string {
  if (percent === null) return '—'
  if (percent === 0) return '0%'
  return percent < 0 ? `−${Math.abs(percent)}%` : `+${percent}%`
}

export interface MediaTitle {
  primary: string | null
  episode: string | null
  secondary: string | null
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
  if (sizeChangePercent(sourceBytes, outputBytes) === null) return null
  const scale = Math.max(sourceBytes, outputBytes)
  if (scale <= 0) return null
  return { source: (sourceBytes / scale) * 100, output: (outputBytes / scale) * 100 }
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
