// Typed client for the Optimisarr API. All HTTP lives here, not in components.

const ADMIN_TOKEN_KEY = 'optimisarr.adminToken'

let authRequiredHandler: (() => void) | null = null

export class AuthRequiredError extends Error {
  constructor(message = 'Admin token required.') {
    super(message)
    this.name = 'AuthRequiredError'
  }
}

export function getAdminToken(): string | null {
  if (typeof localStorage === 'undefined') return null
  const token = localStorage.getItem(ADMIN_TOKEN_KEY)
  return token && token.trim().length > 0 ? token : null
}

export function setAdminToken(token: string) {
  if (typeof localStorage === 'undefined') return
  localStorage.setItem(ADMIN_TOKEN_KEY, token)
}

export function clearAdminToken() {
  if (typeof localStorage === 'undefined') return
  localStorage.removeItem(ADMIN_TOKEN_KEY)
}

export function setAuthRequiredHandler(handler: (() => void) | null) {
  authRequiredHandler = handler
}

export type Health = {
  status: string
  service: string
  version: string | null
  checkedAt: string
}

export type AuthStatus = {
  required: boolean
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
  reencodeSameCodecAboveBytes: number | null
  skipEfficientSources: boolean
  targetVideoCodec: string | null
  targetContainer: string | null
  hdrHandling: string | null
  excludePaths: string | null
  qualityCrf: number | null
  encoderPreset: string | null
  audioTargetCodec: string | null
  audioBitrateKbps: number | null
  videoAudioCodec: string | null
  videoAudioBitrateKbps: number | null
  downmixToStereo: boolean
  reencodeLossyAudio: boolean
  targetImageFormat: string | null
  imageQuality: number | null
  reencodeLossyImages: boolean
  imageDownscaleMode: string
  imageDownscaleValue: number
  moveOnComplete: boolean
  targetFolder: string | null
  moveOverwrite: boolean
  minVmafHarmonicMean: number | null
  minVmafMin: number | null
  autoEnqueueEnabled: boolean
  autoEnqueueWindowStart: string
  autoEnqueueWindowEnd: string
  autoReplace: boolean
}

export type LibraryAccess = {
  path: string
  exists: boolean
  readable: boolean
  writable: boolean
  ok: boolean
  message: string
}

export type Library = LibraryRules & {
  id: number
  name: string
  path: string
  mediaType: string
  ruleProfile: string
  enabled: boolean
  lastAutoEnqueueAt: string | null
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

export type RuleProfileSpec = {
  profile: string
  codec: string | null
  container: string | null
  crf: number | null
}

export type LibraryOptions = {
  mediaTypes: string[]
  ruleProfiles: string[]
  ruleProfileSpecs: RuleProfileSpec[]
  hdrHandlings: string[]
  videoCodecs: string[]
  containers: string[]
  encoderPresets: string[]
  imageFormats: string[]
}

export type Settings = {
  maxConcurrentJobs: number
  minFreeDiskBytes: number
  cpuThreadLimit: number
  libraryScanIntervalHours: number
  encoderMode: string
  hardwareDecode: boolean
  verificationDurationTolerancePercent: number
  verificationRequireAudioRetained: boolean
  verificationRequireSubtitlesRetained: boolean
  verificationRequireSizeReduction: boolean
  verificationQualityGateEnabled: boolean
  verificationMinimumVmafHarmonicMean: number
  verificationMinimumVmafMin: number
  verificationAudioLoudnessGateEnabled: boolean
  verificationMaxLoudnessDriftLufs: number
  verificationAudioClippingGateEnabled: boolean
  verificationMaxTruePeakDbtp: number
  verificationImageQualityGateEnabled: boolean
  verificationMinimumImageSsim: number
  verificationImageMetadataGateEnabled: boolean
  replacementAllowCrossFilesystem: boolean
  dryRunMode: boolean
  replacementQuarantineRetentionDays: number
}

export type ConfigSnapshot = {
  version: number
  exportedAt: string
  settings: Record<string, string>
  libraries: unknown[]
  activityWatchers: unknown[]
  notificationTargets: unknown[]
  arrConnections: unknown[]
}

export type ConfigImportResult = {
  applied: boolean
  errors: string[]
  librariesCreated: number
  librariesUpdated: number
  watchersCreated: number
  watchersUpdated: number
  targetsCreated: number
  targetsUpdated: number
  arrConnectionsCreated: number
  arrConnectionsUpdated: number
  settingsApplied: number
}

export type QueueStatus = Settings & {
  canStart: boolean
  blockedReason: string | null
  runningJobs: number
  // True when at least one running job is using a hardware (GPU) video encoder.
  hardwareAccelerated: boolean
  freeDiskBytes: number | null
  workRoot: string
  // Set when dispatch is ready but nothing starts because every queued job's library auto-optimise
  // window is shut, e.g. "1605 job(s) waiting for the TV optimise window (00:00–05:00)".
  waitingReason: string | null
}

export type Stats = {
  bytesSaved: number
  originalBytes: number
  optimisedBytes: number
  filesOptimised: number
  averageSavingPercent: number
  inQuarantine: number
  quarantineReclaimableBytes: number
  queued: number
  running: number
  readyToReplace: number
  failed: number
  libraries: number
  enabledLibraries: number
  discoveredFiles: number
}

export type VerificationCheck = {
  name: string
  outcome: 'Passed' | 'Failed'
  detail: string
}

export type VerificationReport = {
  checks: VerificationCheck[]
}

export type MediaSideStats = {
  sizeBytes: number | null
  container: string | null
  videoCodec: string | null
  width: number | null
  height: number | null
  durationSeconds: number | null
  audioChannels: number | null
  audioCodec: string | null
  audioBitrateKbps: number | null
}

export type PreviewComparison = {
  jobId: number
  mediaFileId: number
  mediaKind: string
  status: string
  progress: number
  errorMessage: string | null
  original: MediaSideStats | null
  encoded: MediaSideStats | null
  savingPercent: number | null
  clipped: boolean
  verificationPassed: boolean | null
  verificationReportJson: string | null
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
  failureCategory: string | null
  ffmpegArguments: string | null
  videoEncoder: string | null
  outputSizeBytes: number | null
  verificationPassed: boolean | null
  verificationReportJson: string | null
  verifiedAt: string | null
  enqueuedAt: string
  startedAt: string | null
  finishedAt: string | null
  clearable: boolean
}

export type EnqueueResult = {
  enqueued: number
  alreadyQueued: number
  ineligible: number
  importing: number
}

export type FailureSample = {
  jobId: number
  mediaFileId: number
  relativePath: string | null
  errorMessage: string | null
}

export type FailureGroup = {
  category: string
  description: string
  count: number
  samples: FailureSample[]
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

export type ReplacementDetail = Replacement & {
  mediaKind: string
  verificationPassed: boolean | null
  verificationReportJson: string | null
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

export type NotificationType = 'Webhook' | 'Discord' | 'Ntfy' | 'Apprise'

export type NotificationTarget = {
  id: number
  name: string
  type: NotificationType
  url: string
  hasToken: boolean
  enabled: boolean
  notifyOnReplacement: boolean
  notifyOnFailure: boolean
  createdAt: string
  updatedAt: string
}

export type SaveNotificationTarget = {
  name: string
  type: NotificationType
  url: string
  token: string
  enabled: boolean
  notifyOnReplacement: boolean
  notifyOnFailure: boolean
}

export type ArrConnectionType = 'Sonarr' | 'Radarr'

export type ArrConnection = {
  id: number
  name: string
  type: ArrConnectionType
  baseUrl: string
  hasApiKey: boolean
  enabled: boolean
  createdAt: string
  updatedAt: string
}

export type SaveArrConnection = {
  name: string
  type: ArrConnectionType
  baseUrl: string
  apiKey: string
  enabled: boolean
}

export type PlexConnectStart = { id: number; code: string; authUrl: string }
export type JellyfinConnectStart = { code: string; secret: string }
export type ConnectResult = { authorized: boolean; token: string | null }
export type ConnectionTestResult = { ok: boolean; serverName: string | null; version: string | null; error: string | null }
export type PlexDiscoveredServer = { name: string; uri: string; local: boolean; accessToken: string | null }

export type MediaFile = {
  id: number
  libraryId: number
  relativePath: string
  sizeBytes: number
  status: string
  mediaKind: string
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
  mediaKind: string
  codec: string | null
  profile: string
  eligible: boolean
  reason: string
}

export type CandidateSummary = {
  libraryId: number
  eligible: number
  skipped: number
}

export type InventoryFilter = 'all' | 'eligible' | 'skipped' | 'unprobed'

export type InventoryRow = {
  file: MediaFile
  eligible: boolean | null
  reason: string | null
}

export type InventoryCounts = {
  all: number
  eligible: number
  skipped: number
  unprobed: number
}

export type InventoryPage = {
  items: InventoryRow[]
  total: number
  counts: InventoryCounts
}

export type ScanSummary = {
  discovered: number
  added: number
  updated: number
  skippedUnsettled: number
}

export type Exclusion = {
  id: number
  path: string
  libraryId: number | null
  relativePath: string | null
  reason: string | null
  source: string
  createdAt: string
}

export type BrowseResponse = {
  path: string
  parent: string | null
  directories: { name: string; path: string }[]
}

function authorizedHeaders(init?: RequestInit): Headers {
  const headers = new Headers(init?.headers)
  const token = getAdminToken()
  if (token) headers.set('authorization', `Bearer ${token}`)
  if (init?.body && !headers.has('content-type')) headers.set('content-type', 'application/json')
  return headers
}

function authenticatedUrl(url: string): string {
  const token = getAdminToken()
  if (!token) return url
  const separator = url.includes('?') ? '&' : '?'
  return `${url}${separator}access_token=${encodeURIComponent(token)}`
}

function handleAuthRequired(): never {
  clearAdminToken()
  authRequiredHandler?.()
  throw new AuthRequiredError()
}

function tryParseJson(text: string): unknown {
  try {
    return JSON.parse(text)
  } catch {
    return null
  }
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, {
    ...init,
    headers: authorizedHeaders(init),
  })

  const text = await response.text()
  const payload = text ? tryParseJson(text) : null

  if (response.status === 401) handleAuthRequired()

  if (!response.ok) {
    throw new Error(
      payload && typeof payload === 'object' && 'error' in payload
        ? String(payload.error)
        : `Request failed with ${response.status}`
    )
  }

  return payload as T
}

export const api = {
  health: () => request<Health>('/api/health'),
  authStatus: () => request<AuthStatus>('/api/auth/status'),
  tools: () => request<{ tools: ToolCheck[] }>('/api/system/tools').then((r) => r.tools),
  hardware: (refresh = false) =>
    request<{ hardware: HardwareCapability }>(`/api/system/hardware${refresh ? '?refresh=true' : ''}`).then(
      (r) => r.hardware
    ),

  libraryOptions: () => request<LibraryOptions>('/api/library-options'),
  libraries: () => request<Library[]>('/api/libraries'),
  libraryAccess: (id: number) => request<LibraryAccess>(`/api/libraries/${id}/access`),
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

  // Settings preview: a throwaway transcode for original-vs-encoded comparison.
  createPreview: (mediaFileId: number) =>
    request<{ jobId: number }>(`/api/media/${mediaFileId}/preview`, { method: 'POST' }),
  getPreview: (jobId: number) => request<PreviewComparison>(`/api/preview/${jobId}`),
  deletePreview: (jobId: number) => request<void>(`/api/preview/${jobId}`, { method: 'DELETE' }),
  mediaContentUrl: (mediaFileId: number) => authenticatedUrl(`/api/media/${mediaFileId}/content`),
  previewContentUrl: (jobId: number) => authenticatedUrl(`/api/preview/${jobId}/content`),

  candidates: (libraryId?: number) =>
    request<Candidate[]>(`/api/candidates${libraryId ? `?libraryId=${libraryId}` : ''}`),
  candidateSummary: () => request<CandidateSummary[]>('/api/candidates/summary'),

  // The paginated Inventory view: a page of files with their verdicts, plus per-filter counts.
  inventory: (params: { libraryId?: number; show?: InventoryFilter; search?: string; page?: number; pageSize?: number }) => {
    const q = new URLSearchParams()
    if (params.libraryId !== undefined) q.set('libraryId', String(params.libraryId))
    if (params.show && params.show !== 'all') q.set('show', params.show)
    if (params.search) q.set('search', params.search)
    if (params.page !== undefined) q.set('page', String(params.page))
    if (params.pageSize !== undefined) q.set('pageSize', String(params.pageSize))
    const query = q.toString()
    return request<InventoryPage>(`/api/inventory${query ? `?${query}` : ''}`)
  },

  // Exclusions: files the operator never wants optimised again (durable, path-keyed).
  exclusions: (libraryId?: number) =>
    request<Exclusion[]>(`/api/exclusions${libraryId != null ? `?libraryId=${libraryId}` : ''}`),
  excludeFile: (mediaFileId: number, reason?: string) =>
    request<Exclusion>('/api/exclusions', { method: 'POST', body: JSON.stringify({ mediaFileId, reason }) }),
  removeExclusion: (id: number) => request<void>(`/api/exclusions/${id}`, { method: 'DELETE' }),

  settings: () => request<Settings>('/api/settings'),
  saveSettings: (body: Settings) =>
    request<Settings>('/api/settings', { method: 'PUT', body: JSON.stringify(body) }),
  queueStatus: () => request<QueueStatus>('/api/queue/status'),
  exportSettings: () => request<ConfigSnapshot>('/api/settings/export'),
  importSettings: (snapshot: ConfigSnapshot) =>
    request<ConfigImportResult>('/api/settings/import', { method: 'POST', body: JSON.stringify(snapshot) }),

  activityWatchers: () => request<ActivityWatcher[]>('/api/activity-watchers'),
  createActivityWatcher: (body: SaveActivityWatcher) =>
    request<ActivityWatcher>('/api/activity-watchers', { method: 'POST', body: JSON.stringify(body) }),
  updateActivityWatcher: (id: number, body: SaveActivityWatcher) =>
    request<ActivityWatcher>(`/api/activity-watchers/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  deleteActivityWatcher: (id: number) =>
    request<void>(`/api/activity-watchers/${id}`, { method: 'DELETE' }),

  notificationTargets: () => request<NotificationTarget[]>('/api/notification-targets'),
  createNotificationTarget: (body: SaveNotificationTarget) =>
    request<NotificationTarget>('/api/notification-targets', { method: 'POST', body: JSON.stringify(body) }),
  updateNotificationTarget: (id: number, body: SaveNotificationTarget) =>
    request<NotificationTarget>(`/api/notification-targets/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  deleteNotificationTarget: (id: number) =>
    request<void>(`/api/notification-targets/${id}`, { method: 'DELETE' }),

  arrConnections: () => request<ArrConnection[]>('/api/arr-connections'),
  createArrConnection: (body: SaveArrConnection) =>
    request<ArrConnection>('/api/arr-connections', { method: 'POST', body: JSON.stringify(body) }),
  updateArrConnection: (id: number, body: SaveArrConnection) =>
    request<ArrConnection>(`/api/arr-connections/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  deleteArrConnection: (id: number) =>
    request<void>(`/api/arr-connections/${id}`, { method: 'DELETE' }),

  plexConnectStart: () => request<PlexConnectStart>('/api/connect/plex/start', { method: 'POST' }),
  plexConnectPoll: (id: number) => request<ConnectResult>(`/api/connect/plex/poll?id=${id}`),
  jellyfinConnectStart: (baseUrl: string) =>
    request<JellyfinConnectStart>('/api/connect/jellyfin/start', { method: 'POST', body: JSON.stringify({ baseUrl }) }),
  jellyfinConnectPoll: (baseUrl: string, secret: string) =>
    request<ConnectResult>('/api/connect/jellyfin/poll', { method: 'POST', body: JSON.stringify({ baseUrl, secret }) }),
  plexServers: (token: string) =>
    request<PlexDiscoveredServer[]>('/api/connect/plex/servers', { method: 'POST', body: JSON.stringify({ token }) }),
  testConnection: (body: { type: ActivityWatcherType; baseUrl: string; token?: string; id?: number }) =>
    request<ConnectionTestResult>('/api/connect/test', { method: 'POST', body: JSON.stringify(body) }),

  jobs: () => request<Job[]>('/api/jobs'),
  jobFailures: () => request<FailureGroup[]>('/api/jobs/failures'),
  // The captured ffmpeg log is plain text, and 404s when a job has none — return null rather than throw.
  jobLog: async (id: number): Promise<string | null> => {
    const response = await fetch(`/api/jobs/${id}/log`, { headers: authorizedHeaders() })
    if (response.status === 404) return null
    if (response.status === 401) handleAuthRequired()
    if (!response.ok) throw new Error(`Request failed with ${response.status}`)
    return response.text()
  },
  cancelJob: (id: number) => request<{ id: number; status: string }>(`/api/jobs/${id}/cancel`, { method: 'POST' }),
  removeJob: (id: number) => request<void>(`/api/jobs/${id}`, { method: 'DELETE' }),
  retryJob: (id: number) => request<{ id: number; status: string }>(`/api/jobs/${id}/retry`, { method: 'POST' }),
  clearJobs: (scope?: 'errored' | 'finished' | 'all') =>
    request<{ cleared: number }>(`/api/jobs/clear${scope ? `?scope=${scope}` : ''}`, { method: 'POST' }),
  clearPendingJobs: () => request<{ cleared: number }>('/api/jobs/clear-pending', { method: 'POST' }),
  enqueueLibrary: (id: number) =>
    request<EnqueueResult>(`/api/libraries/${id}/enqueue`, { method: 'POST' }),
  replaceFromJob: (id: number) =>
    request<Replacement>(`/api/jobs/${id}/replace`, { method: 'POST' }),

  replacements: () => request<Replacement[]>('/api/replacements'),
  replacement: (id: number) => request<ReplacementDetail>(`/api/replacements/${id}`),
  replacementOriginalContentUrl: (id: number) => authenticatedUrl(`/api/replacements/${id}/original/content`),
  replacementReplacementContentUrl: (id: number) => authenticatedUrl(`/api/replacements/${id}/replacement/content`),
  rollbackReplacement: (id: number) =>
    request<Replacement>(`/api/replacements/${id}/rollback`, { method: 'POST' }),
  approveReplacement: (id: number) =>
    request<Replacement>(`/api/replacements/${id}/approve`, { method: 'POST' }),
  clearReplacements: () => request<{ cleared: number }>('/api/replacements/clear', { method: 'POST' }),
  stats: () => request<Stats>('/api/stats'),
  // Reset the persistent lifetime "total space saved" tally; returns the freshly zeroed figures.
  clearStats: () => request<Stats>('/api/stats/clear', { method: 'POST' }),
}
