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

// A candidate that grew saved nothing; it is never shown as a negative saving.
export function savedPercent(sourceBytes: number, outputBytes: number): number {
  if (sourceBytes <= 0 || outputBytes >= sourceBytes) return 0
  return Math.round((1 - outputBytes / sourceBytes) * 100)
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
