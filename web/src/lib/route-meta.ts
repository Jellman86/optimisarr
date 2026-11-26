import type { Messages } from './i18n/en.ts'

// What each hash route is called, kept beside the routes themselves so the browser tab, the
// history list and a screen reader's window title all name the page a person is on. Pure so
// the mapping is testable without a browser; the caller passes the active locale's messages.

export const APP_TITLE = 'Optimisarr'

export type SettingsRoomKey =
  | 'encoding' | 'superResolution' | 'files' | 'servers' | 'downloads' | 'notifications' | 'workers' | 'system'

export const SETTINGS_ROOM_PATHS: Record<SettingsRoomKey, string> = {
  encoding: 'encoding',
  superResolution: 'super-resolution',
  files: 'files',
  servers: 'media-servers',
  downloads: 'download-managers',
  notifications: 'notifications',
  workers: 'workers',
  system: 'system',
}

function cleanPath(path: string): string {
  return path.split(/[?#]/)[0].replace(/\/+$/, '') || '/'
}

export function settingsRoomFromPath(path: string): SettingsRoomKey | null {
  const clean = cleanPath(path)
  // The old /tools route, and anything linking to it, lands in the room that absorbed it.
  if (/^\/tools(?:\/|$)/.test(clean)) return 'system'
  const match = clean.match(/^\/settings\/([^/]+)$/)
  if (!match) return null
  const found = (Object.entries(SETTINGS_ROOM_PATHS) as [SettingsRoomKey, string][]).find(([, slug]) => slug === match[1])
  return found ? found[0] : null
}

export function settingsRoomTitle(room: SettingsRoomKey, m: Messages): string {
  const titles: Record<SettingsRoomKey, string> = {
    encoding: m.settings.room_encoding,
    superResolution: m.settings.room_super_resolution,
    files: m.settings.room_files,
    servers: m.settings.room_servers,
    downloads: m.settings.room_downloads,
    notifications: m.settings.room_notifications,
    workers: m.settings.room_workers,
    system: m.settings.room_system,
  }
  return titles[room]
}

/** The pages the shell can show. Each is loaded only when first visited, so the name is what the
 *  shell resolves before it has the component itself. */
export type PageName =
  | 'dashboard' | 'libraries' | 'qualityLab' | 'exactDuplicates' | 'inventory' | 'optimiseFile'
  | 'queue' | 'quarantine' | 'schedule' | 'settings'

export function pageNameFor(path: string): PageName {
  if (/^\/libraries\/\d+\/quality-check$/.test(path)) return 'qualityLab'
  if (/^\/libraries\/\d+\/duplicates$/.test(path)) return 'exactDuplicates'
  if (path.startsWith('/libraries')) return 'libraries'
  // Inventory absorbed the Candidates view; the old /candidates route still lands there.
  if (/^\/inventory\/optimise\/\d+$/.test(path)) return 'optimiseFile'
  if (path === '/inventory/duplicates') return 'exactDuplicates'
  if (path.startsWith('/inventory') || path.startsWith('/candidates')) return 'inventory'
  if (path.startsWith('/queue')) return 'queue'
  if (path.startsWith('/quarantine')) return 'quarantine'
  if (path.startsWith('/schedule')) return 'schedule'
  // Tools moved into Settings; the old route still lands there (opens the Tools tab).
  if (path.startsWith('/tools') || path.startsWith('/settings')) return 'settings'
  return 'dashboard'
}

/** True when the rendered page already displays live work, so the sidebar omits NowEncoding. */
export function showsActiveWork(path: string): boolean {
  const clean = cleanPath(path)
  if (clean.startsWith('/queue')) return true
  if (
    clean.startsWith('/libraries')
    || clean.startsWith('/inventory')
    || clean.startsWith('/candidates')
    || clean.startsWith('/quarantine')
    || clean.startsWith('/schedule')
    || clean.startsWith('/settings')
    || clean.startsWith('/tools')
  ) {
    return false
  }
  // Everything else falls back to the Dashboard, which renders InFlightPanel.
  return true
}

/** The page's name, then the section it sits inside, most specific first. */
function titleParts(path: string, m: Messages): string[] {
  const clean = cleanPath(path)
  if (/^\/libraries\/new(?:\/|$)/.test(clean)) return [m.libraries.add_library_heading, m.nav.libraries]
  if (/^\/libraries\/\d+\/configure(?:\/|$)/.test(clean)) return [m.libraries.configure, m.nav.libraries]
  if (/^\/libraries\/\d+\/quality-check$/.test(clean)) return [m.calibration.eyebrow, m.nav.libraries]
  if (/^\/libraries\/\d+\/duplicates$/.test(clean)) return [m.duplicates.title, m.nav.libraries]
  if (clean.startsWith('/libraries')) return [m.nav.libraries]
  if (/^\/inventory\/optimise\/\d+$/.test(clean)) return [m.onePlan.title, m.nav.inventory]
  if (clean === '/inventory/duplicates') return [m.duplicates.title, m.nav.inventory]
  if (clean.startsWith('/inventory') || clean.startsWith('/candidates')) return [m.nav.inventory]
  if (clean.startsWith('/queue')) return [m.nav.queue]
  if (/^\/quarantine\/\d+$/.test(clean)) return [m.quarantine.review_title, m.nav.quarantine]
  if (clean.startsWith('/quarantine')) return [m.nav.quarantine]
  if (clean.startsWith('/schedule')) return [m.nav.schedule]
  if (clean.startsWith('/settings') || clean.startsWith('/tools')) {
    const room = settingsRoomFromPath(clean)
    return room ? [settingsRoomTitle(room, m), m.nav.settings] : [m.nav.settings]
  }
  return [m.nav.dashboard]
}

export function pageTitle(path: string, m: Messages): string {
  return [...titleParts(path, m), APP_TITLE].join(' · ')
}
