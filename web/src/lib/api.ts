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
  enqueuedAt: string
  startedAt: string | null
  finishedAt: string | null
}

export type EnqueueResult = {
  enqueued: number
  alreadyQueued: number
  ineligible: number
}

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

  jobs: () => request<Job[]>('/api/jobs'),
  cancelJob: (id: number) => request<{ id: number; status: string }>(`/api/jobs/${id}/cancel`, { method: 'POST' }),
  enqueueLibrary: (id: number) =>
    request<EnqueueResult>(`/api/libraries/${id}/enqueue`, { method: 'POST' }),
}
