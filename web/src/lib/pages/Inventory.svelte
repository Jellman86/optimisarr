<script lang="ts">
  import { api, type Library, type MediaFile } from '../api'
  import { formatSize, formatDuration } from '../format'
  import Banner from '../components/Banner.svelte'

  let libraries = $state<Library[]>([])
  let files = $state<MediaFile[]>([])
  let selectedLibrary = $state<number | 'all'>('all')
  let error = $state<string | null>(null)
  let probingId = $state<number | null>(null)
  let loading = $state(true)

  $effect(() => {
    void loadLibraries()
  })

  // Reload media whenever the library filter changes.
  $effect(() => {
    void loadMedia(selectedLibrary)
  })

  async function loadLibraries() {
    try {
      libraries = await api.libraries()
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to load libraries'
    }
  }

  async function loadMedia(filter: number | 'all') {
    loading = true
    error = null
    try {
      files = await api.media(filter === 'all' ? undefined : filter)
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to load media'
    } finally {
      loading = false
    }
  }

  async function probe(file: MediaFile) {
    probingId = file.id
    error = null
    try {
      const updated = await api.probe(file.id)
      files = files.map((f) => (f.id === file.id ? updated : f))
    } catch (err) {
      error = err instanceof Error ? err.message : 'Probe failed'
    } finally {
      probingId = null
    }
  }

  function resolution(file: MediaFile) {
    return file.width && file.height ? `${file.width}×${file.height}` : '—'
  }
</script>

<header class="mb-6 flex flex-wrap items-end justify-between gap-4">
  <div>
    <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">Inventory</h1>
    <p class="text-sm text-slate-500 dark:text-slate-400">Discovered media. Probing reads codec, resolution, and duration — nothing is modified.</p>
  </div>
  <div>
    <label class="label" for="lib-filter">Library</label>
    <select id="lib-filter" class="input min-w-48" bind:value={selectedLibrary}>
      <option value="all">All libraries</option>
      {#each libraries as library}<option value={library.id}>{library.name}</option>{/each}
    </select>
  </div>
</header>

{#if error}
  <Banner kind="error" class="mb-4">{error}</Banner>
{/if}

{#if loading}
  <div class="card p-8 text-center text-slate-400">Loading…</div>
{:else if files.length > 0}
  <div class="card overflow-x-auto">
    <table class="w-full text-sm">
      <thead class="border-b border-slate-200 text-left text-xs uppercase text-slate-500 dark:border-slate-700 dark:text-slate-400">
        <tr>
          <th class="px-4 py-3">File</th>
          <th class="hidden px-4 py-3 lg:table-cell">Kind</th>
          <th class="px-4 py-3">Size</th>
          <th class="hidden px-4 py-3 sm:table-cell">Video</th>
          <th class="hidden px-4 py-3 sm:table-cell">Resolution</th>
          <th class="hidden px-4 py-3 lg:table-cell">Audio</th>
          <th class="hidden px-4 py-3 md:table-cell">Subs</th>
          <th class="hidden px-4 py-3 md:table-cell">Duration</th>
          <th class="px-4 py-3"></th>
        </tr>
      </thead>
      <tbody class="divide-y divide-slate-100 dark:divide-slate-800">
        {#each files as file (file.id)}
          <tr class="text-slate-700 dark:text-slate-300">
            <td class="max-w-[55vw] truncate px-4 py-2 font-mono text-xs sm:max-w-xs" title={file.relativePath}>{file.relativePath}</td>
            <td class="hidden px-4 py-2 lg:table-cell">
              {#if file.mediaKind && file.mediaKind !== 'Unknown'}
                <span class="badge bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300">{file.mediaKind}</span>
              {:else}
                <span class="text-slate-400">—</span>
              {/if}
            </td>
            <td class="px-4 py-2">{formatSize(file.sizeBytes)}</td>
            <td class="hidden px-4 py-2 sm:table-cell">{file.videoCodec ?? '—'}</td>
            <td class="hidden px-4 py-2 sm:table-cell">{resolution(file)}</td>
            <td class="hidden max-w-32 truncate px-4 py-2 lg:table-cell" title={file.audioCodecs ?? ''}>{file.audioCodecs ?? '—'}</td>
            <td class="hidden px-4 py-2 md:table-cell">{file.subtitleTrackCount ?? '—'}</td>
            <td class="hidden px-4 py-2 md:table-cell">{formatDuration(file.durationSeconds)}</td>
            <td class="px-4 py-2">
              <div class="flex items-center gap-2">
                <button class="btn px-3 py-1 text-xs" onclick={() => probe(file)} disabled={probingId === file.id}>
                  {probingId === file.id ? 'Probing' : file.status === 'Discovered' ? 'Probe' : 'Re-probe'}
                </button>
                {#if file.probeError}
                  <span class="text-xs text-red-600" title={file.probeError}>failed</span>
                {/if}
              </div>
            </td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
  <p class="mt-2 text-xs text-slate-400">{files.length.toLocaleString()} files</p>
{:else}
  <div class="card p-8 text-center text-slate-500 dark:text-slate-400">
    No media here yet. Add a library and scan it from the Libraries page.
  </div>
{/if}
