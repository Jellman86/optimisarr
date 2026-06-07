<script lang="ts">
  type HealthResponse = {
    status: string
    service: string
    version: string | null
    checkedAt: string
  }

  type ToolCheck = {
    name: string
    command: string
    available: boolean
    version: string | null
    error: string | null
  }

  type ToolsResponse = {
    checkedAt: string
    tools: ToolCheck[]
  }

  type MediaFile = {
    id: number
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

  type ScanSummary = {
    discovered: number
    added: number
    updated: number
    skippedUnsettled: number
  }

  let health = $state<HealthResponse | null>(null)
  let tools = $state<ToolCheck[]>([])
  let loading = $state(true)
  let error = $state<string | null>(null)

  let libraryRoot = $state('')
  let savedLibraryRoot = $state<string | null>(null)
  let mediaFiles = $state<MediaFile[]>([])
  let libraryBusy = $state(false)
  let libraryMessage = $state<string | null>(null)
  let libraryError = $state<string | null>(null)
  let probingId = $state<number | null>(null)

  const stages = [
    'Discover',
    'Probe',
    'Plan',
    'Transcode',
    'Verify',
    'Quarantine',
    'Replace'
  ]

  $effect(() => {
    void loadStatus()
    void loadLibrary()
  })

  async function loadStatus() {
    loading = true
    error = null

    try {
      const [healthResponse, toolsResponse] = await Promise.all([
        fetch('/api/health'),
        fetch('/api/system/tools')
      ])

      if (!healthResponse.ok) {
        throw new Error(`Health check failed with ${healthResponse.status}`)
      }

      if (!toolsResponse.ok) {
        throw new Error(`Tool check failed with ${toolsResponse.status}`)
      }

      health = await healthResponse.json() as HealthResponse
      const toolPayload = await toolsResponse.json() as ToolsResponse
      tools = toolPayload.tools
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to load service status'
    } finally {
      loading = false
    }
  }

  async function loadLibrary() {
    try {
      const settingsResponse = await fetch('/api/settings')
      if (settingsResponse.ok) {
        const settings = await settingsResponse.json() as { libraryRoot: string | null }
        savedLibraryRoot = settings.libraryRoot
        if (settings.libraryRoot && !libraryRoot) {
          libraryRoot = settings.libraryRoot
        }
      }

      await loadMedia()
    } catch (err) {
      libraryError = err instanceof Error ? err.message : 'Unable to load library'
    }
  }

  async function loadMedia() {
    const response = await fetch('/api/media')
    if (response.ok) {
      mediaFiles = await response.json() as MediaFile[]
    }
  }

  async function saveLibraryRoot() {
    libraryBusy = true
    libraryError = null
    libraryMessage = null

    try {
      const response = await fetch('/api/settings/library-root', {
        method: 'PUT',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ path: libraryRoot })
      })

      const payload = await response.json()
      if (!response.ok) {
        throw new Error(payload.error ?? `Save failed with ${response.status}`)
      }

      savedLibraryRoot = payload.libraryRoot
      libraryMessage = 'Library root saved.'
    } catch (err) {
      libraryError = err instanceof Error ? err.message : 'Unable to save library root'
    } finally {
      libraryBusy = false
    }
  }

  async function scanLibrary() {
    libraryBusy = true
    libraryError = null
    libraryMessage = null

    try {
      const response = await fetch('/api/library/scan', { method: 'POST' })
      const payload = await response.json()
      if (!response.ok) {
        throw new Error(payload.error ?? `Scan failed with ${response.status}`)
      }

      const summary = payload as ScanSummary
      libraryMessage = `Scan complete: ${summary.discovered} found, ${summary.added} new, ${summary.updated} updated, ${summary.skippedUnsettled} still settling.`
      await loadMedia()
    } catch (err) {
      libraryError = err instanceof Error ? err.message : 'Unable to scan library'
    } finally {
      libraryBusy = false
    }
  }

  async function probeFile(file: MediaFile) {
    probingId = file.id
    libraryError = null

    try {
      const response = await fetch(`/api/media/${file.id}/probe`, { method: 'POST' })
      const payload = await response.json()
      if (!response.ok) {
        throw new Error(payload.error ?? `Probe failed with ${response.status}`)
      }

      mediaFiles = mediaFiles.map((existing) => existing.id === file.id ? payload as MediaFile : existing)
    } catch (err) {
      libraryError = err instanceof Error ? err.message : 'Unable to probe file'
    } finally {
      probingId = null
    }
  }

  function formatSize(bytes: number) {
    const units = ['B', 'KB', 'MB', 'GB', 'TB']
    let value = bytes
    let unit = 0
    while (value >= 1024 && unit < units.length - 1) {
      value /= 1024
      unit++
    }
    return `${value.toFixed(value < 10 && unit > 0 ? 1 : 0)} ${units[unit]}`
  }

  function formatDuration(seconds: number | null) {
    if (seconds === null) {
      return '—'
    }
    const total = Math.round(seconds)
    const hours = Math.floor(total / 3600)
    const minutes = Math.floor((total % 3600) / 60)
    return hours > 0 ? `${hours}h ${minutes}m` : `${minutes}m`
  }

  function resolution(file: MediaFile) {
    return file.width && file.height ? `${file.width}×${file.height}` : '—'
  }
</script>

<svelte:head>
  <title>Optimisarr</title>
</svelte:head>

<main class="shell">
  <header class="topbar">
    <div>
      <p class="eyebrow">Media library optimiser</p>
      <h1>Optimisarr</h1>
    </div>
    <button type="button" onclick={loadStatus} disabled={loading}>
      {loading ? 'Checking' : 'Refresh'}
    </button>
  </header>

  {#if error}
    <section class="notice error">
      <strong>Status check failed</strong>
      <span>{error}</span>
    </section>
  {/if}

  <section class="status-grid" aria-label="Service status">
    <article>
      <span class="label">Service</span>
      <strong>{health?.service ?? 'optimisarr'}</strong>
      <small>{health?.status ?? 'unknown'}</small>
    </article>
    <article>
      <span class="label">Database</span>
      <strong>SQLite</strong>
      <small>/config/optimisarr.db</small>
    </article>
    <article>
      <span class="label">Worker</span>
      <strong>Idle</strong>
      <small>queue not initialised</small>
    </article>
    <article>
      <span class="label">Safety</span>
      <strong>Verify first</strong>
      <small>replacement disabled until checks pass</small>
    </article>
  </section>

  <section class="panel">
    <div class="panel-heading">
      <div>
        <h2>Media Tools</h2>
        <p>FFmpeg and ffprobe must be available before scanning and verification work can begin.</p>
      </div>
    </div>

    <div class="tool-list">
      {#each tools as tool}
        <article class:available={tool.available}>
          <div>
            <strong>{tool.name}</strong>
            <code>{tool.command}</code>
          </div>
          <span>{tool.available ? 'Available' : 'Missing'}</span>
          <small>{tool.version ?? tool.error}</small>
        </article>
      {/each}

      {#if !loading && tools.length === 0}
        <p class="empty">No tool checks returned.</p>
      {/if}
    </div>
  </section>

  <section class="panel">
    <div class="panel-heading">
      <div>
        <h2>Library</h2>
        <p>Point Optimisarr at one library root, then scan to build a read-only inventory. Nothing is modified during discovery or probing.</p>
      </div>
    </div>

    <div class="library-controls">
      <label for="library-root">Library root</label>
      <div class="library-input">
        <input
          id="library-root"
          type="text"
          placeholder="/data"
          bind:value={libraryRoot}
          disabled={libraryBusy}
        />
        <button type="button" onclick={saveLibraryRoot} disabled={libraryBusy || !libraryRoot}>
          Save
        </button>
        <button type="button" onclick={scanLibrary} disabled={libraryBusy || !savedLibraryRoot}>
          {libraryBusy ? 'Working' : 'Scan'}
        </button>
      </div>
      {#if savedLibraryRoot}
        <small>Configured root: <code>{savedLibraryRoot}</code></small>
      {/if}
    </div>

    {#if libraryError}
      <p class="notice error inline">{libraryError}</p>
    {:else if libraryMessage}
      <p class="notice ok inline">{libraryMessage}</p>
    {/if}

    {#if mediaFiles.length > 0}
      <div class="table-wrap">
        <table class="media-table">
          <thead>
            <tr>
              <th>File</th>
              <th>Size</th>
              <th>Video</th>
              <th>Resolution</th>
              <th>Audio</th>
              <th>Subs</th>
              <th>Duration</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {#each mediaFiles as file (file.id)}
              <tr>
                <td class="path" title={file.relativePath}>{file.relativePath}</td>
                <td>{formatSize(file.sizeBytes)}</td>
                <td>{file.videoCodec ?? '—'}</td>
                <td>{resolution(file)}</td>
                <td title={file.audioCodecs ?? ''}>{file.audioCodecs ?? '—'}</td>
                <td>{file.subtitleTrackCount ?? '—'}</td>
                <td>{formatDuration(file.durationSeconds)}</td>
                <td>
                  <button
                    type="button"
                    class="probe"
                    onclick={() => probeFile(file)}
                    disabled={probingId === file.id}
                  >
                    {probingId === file.id ? 'Probing' : file.status === 'Discovered' ? 'Probe' : 'Re-probe'}
                  </button>
                  {#if file.probeError}
                    <small class="probe-error" title={file.probeError}>probe failed</small>
                  {/if}
                </td>
              </tr>
            {/each}
          </tbody>
        </table>
      </div>
    {:else}
      <p class="empty">No media discovered yet. Configure a library root and scan to begin.</p>
    {/if}
  </section>

  <section class="panel">
    <div class="panel-heading">
      <div>
        <h2>Pipeline</h2>
        <p>The first build slice stops at discovery and probing before any conversion logic is added.</p>
      </div>
    </div>

    <ol class="pipeline">
      {#each stages as stage, index}
        <li class:active={index < 2}>
          <span>{index + 1}</span>
          <strong>{stage}</strong>
        </li>
      {/each}
    </ol>
  </section>
</main>
