// Typed client for the Optimisarr API. All HTTP lives here, not in components.

export type Health = {
  status: string
  service: string
  version: string | null
  checkedAt: string
}

export type ToolCheck = {
  name: string
  command: string
  available: boolean
  version: string | null
  error: string | null
}

export type EncoderCapability = {
  name: string
  codec: string
  mode: string
  available: boolean
}

export type HardwareCapability = {
  hardwareAccelerators: string[]
  encoders: EncoderCapability[]
  nvidiaRuntimeAvailable: boolean
  driDeviceAvailable: boolean
  error: string | null
}

export type LibraryRules = {
  priority: number
  minFileSizeBytes: number | null
  maxHeight: number | null
  targetVideoCodec: string | null
  targetContainer: string | null
  hdrHandling: string | null
  excludePaths: string | null
  qualityCrf: number | null
  encoderPreset: string | null
  moveOnComplete: boolean
  targetFolder: string | null
}

export type Library = LibraryRules & {
  id: number
  name: string
  path: string
  mediaType: string
  ruleProfile: string
  enabled: boolean
  fileCount: number
  createdAt: string
  updatedAt: string
}

export type SaveLibrary = LibraryRules & {
  name: string
  path: string
  mediaType: string
  ruleProfile: string
  enabled: boolean
}

export type LibraryOptions = {
  mediaTypes: string[]
  ruleProfiles: string[]
  hdrHandlings: string[]
  videoCodecs: string[]
  containers: string[]
  encoderPresets: string[]
}

export type Settings = {
  maxConcurrentJobs: number
  scheduleEnabled: boolean
  scheduleWindowStart: string
  scheduleWindowEnd: string
  minFreeDiskBytes: number
  cpuThreadLimit: number
  encoderMode: string
  verificationDurationTolerancePercent: number
  verificationRequireAudioRetained: boolean
  verificationRequireSubtitlesRetained: boolean
  verificationRequireSizeReduction: boolean
  replacementAllowCrossFilesystem: boolean
  replacementQuarantineRetentionDays: number
}

export type QueueStatus = Settings & {
  canStart: boolean
  blockedReason: string | null
  runningJobs: number
  freeDiskBytes: number | null
  workRoot: string
}

export type VerificationCheck = {
  name: string
  outcome: 'Passed' | 'Failed'
  detail: string
}

export type VerificationReport = {
  checks: VerificationCheck[]
}

export type Job = {
  id: number
  mediaFileId: number
  libraryId: number | null
  relativePath: string | null
  status: string
  priority: number
  progress: number
  errorMessage: string | null
  ffmpegArguments: string | null
  outputSizeBytes: number | null
  verificationPassed: boolean | null
  verificationReportJson: string | null
  verifiedAt: string | null
  enqueuedAt: string
  startedAt: string | null
  finishedAt: string | null
}

export type EnqueueResult = {
  enqueued: number
  alreadyQueued: number
  ineligible: number
}

export type Replacement = {
  id: number
  jobId: number
  mediaFileId: number
  originalPath: string
  quarantinePath: string
  finalPath: string
  originalSizeBytes: number
  newSizeBytes: number
  crossFilesystem: boolean
  status: 'Replaced' | 'RolledBack' | 'Purged'
  replacedAt: string
  rolledBackAt: string | null
  purgedAt: string | null
}

export type ActivityWatcherType = 'Plex' | 'Jellyfin' | 'Emby'

export type ActivityWatcher = {
  id: number
  name: string
  type: ActivityWatcherType
  baseUrl: string
  hasToken: boolean
  enabled: boolean
  refreshOnReplace: boolean
  createdAt: string
  updatedAt: string
}

export type SaveActivityWatcher = {
  name: string
  type: ActivityWatcherType
  baseUrl: string
  apiToken: string
  enabled: boolean
  refreshOnReplace: boolean
}

export type PlexConnectStart = { id: number; code: string; authUrl: string }
export type JellyfinConnectStart = { code: string; secret: string }
export type ConnectResult = { authorized: boolean; token: string | null }

export type MediaFile = {
  id: number
  libraryId: number
  relativePath: string
  sizeBytes: number
  status: string
  container: string | null
  videoCodec: string | null
  width: number | null
  height: number | null
  durationSeconds: number | null
  audioCodecs: string | null
  audioTrackCount: number | null
  subtitleTrackCount: number | null
  probedAt: string | null
  probeError: string | null
}

export type Candidate = {
  mediaFileId: number
  libraryId: number | null
  relativePath: string
  sizeBytes: number
  videoCodec: string | null
  height: number | null
  isHdr: boolean
  profile: string
  eligible: boolean
  reason: string
}

export type ScanSummary = {
  discovered: number
  added: number
  updated: number
  skippedUnsettled: number
}

export type BrowseResponse = {
  path: string
  parent: string | null
  directories: { name: string; path: string }[]
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, {
    ...init,
    headers: init?.body ? { 'content-type': 'application/json', ...init?.headers } : init?.headers,
  })

  const text = await response.text()
  const payload = text ? JSON.parse(text) : null

  if (!response.ok) {
    throw new Error(payload?.error ?? `Request failed with ${response.status}`)
  }

  return payload as T
}

export const api = {
  health: () => request<Health>('/api/health'),
  tools: () => request<{ tools: ToolCheck[] }>('/api/system/tools').then((r) => r.tools),
  hardware: () => request<{ hardware: HardwareCapability }>('/api/system/hardware').then((r) => r.hardware),

  libraryOptions: () => request<LibraryOptions>('/api/library-options'),
  libraries: () => request<Library[]>('/api/libraries'),
  createLibrary: (body: SaveLibrary) =>
    request<Library>('/api/libraries', { method: 'POST', body: JSON.stringify(body) }),
  updateLibrary: (id: number, body: SaveLibrary) =>
    request<Library>(`/api/libraries/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  deleteLibrary: (id: number) =>
    request<void>(`/api/libraries/${id}`, { method: 'DELETE' }),
  scanLibrary: (id: number) =>
    request<ScanSummary>(`/api/libraries/${id}/scan`, { method: 'POST' }),
  scanAll: () => request<ScanSummary>('/api/libraries/scan', { method: 'POST' }),

  browse: (path?: string) =>
    request<BrowseResponse>(`/api/fs/browse${path ? `?path=${encodeURIComponent(path)}` : ''}`),

  media: (libraryId?: number) =>
    request<MediaFile[]>(`/api/media${libraryId ? `?libraryId=${libraryId}` : ''}`),
  probe: (id: number) => request<MediaFile>(`/api/media/${id}/probe`, { method: 'POST' }),

  candidates: (libraryId?: number) =>
    request<Candidate[]>(`/api/candidates${libraryId ? `?libraryId=${libraryId}` : ''}`),

  settings: () => request<Settings>('/api/settings'),
  saveSettings: (body: Settings) =>
    request<Settings>('/api/settings', { method: 'PUT', body: JSON.stringify(body) }),
  queueStatus: () => request<QueueStatus>('/api/queue/status'),

  activityWatchers: () => request<ActivityWatcher[]>('/api/activity-watchers'),
  createActivityWatcher: (body: SaveActivityWatcher) =>
    request<ActivityWatcher>('/api/activity-watchers', { method: 'POST', body: JSON.stringify(body) }),
  updateActivityWatcher: (id: number, body: SaveActivityWatcher) =>
    request<ActivityWatcher>(`/api/activity-watchers/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  deleteActivityWatcher: (id: number) =>
    request<void>(`/api/activity-watchers/${id}`, { method: 'DELETE' }),

  plexConnectStart: () => request<PlexConnectStart>('/api/connect/plex/start', { method: 'POST' }),
  plexConnectPoll: (id: number) => request<ConnectResult>(`/api/connect/plex/poll?id=${id}`),
  jellyfinConnectStart: (baseUrl: string) =>
    request<JellyfinConnectStart>('/api/connect/jellyfin/start', { method: 'POST', body: JSON.stringify({ baseUrl }) }),
  jellyfinConnectPoll: (baseUrl: string, secret: string) =>
    request<ConnectResult>('/api/connect/jellyfin/poll', { method: 'POST', body: JSON.stringify({ baseUrl, secret }) }),

  jobs: () => request<Job[]>('/api/jobs'),
  cancelJob: (id: number) => request<{ id: number; status: string }>(`/api/jobs/${id}/cancel`, { method: 'POST' }),
  enqueueLibrary: (id: number) =>
    request<EnqueueResult>(`/api/libraries/${id}/enqueue`, { method: 'POST' }),
  replaceFromJob: (id: number) =>
    request<Replacement>(`/api/jobs/${id}/replace`, { method: 'POST' }),

  replacements: () => request<Replacement[]>('/api/replacements'),
  rollbackReplacement: (id: number) =>
    request<Replacement>(`/api/replacements/${id}/rollback`, { method: 'POST' }),
}
