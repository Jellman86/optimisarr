<script lang="ts">
  import { api, type Candidate, type Library, type MediaFile } from '../api'
  import { formatSize, formatDuration } from '../format'
  import Banner from '../components/Banner.svelte'

  let libraries = $state<Library[]>([])
  let files = $state<MediaFile[]>([])
  // Eligibility verdict per file (only probed files have one), keyed by media-file id.
  let verdicts = $state<Record<number, Candidate>>({})
  let selectedLibrary = $state<number | 'all'>('all')
  let show = $state<'all' | 'eligible' | 'skipped' | 'unprobed'>('all')
  let error = $state<string | null>(null)
  let probingId = $state<number | null>(null)
  let loading = $state(true)

  $effect(() => {
    void loadLibraries()
  })

  // Reload media (and the eligibility verdicts that overlay it) whenever the filter changes.
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
      await loadVerdicts(filter)
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to load media'
    } finally {
      loading = false
    }
  }

  // Candidates are the same files run through the rules. Best-effort: if this fails the file list
  // still shows, just without eligibility badges.
  async function loadVerdicts(filter: number | 'all') {
    try {
      const candidates = await api.candidates(filter === 'all' ? undefined : filter)
      verdicts = Object.fromEntries(candidates.map((c) => [c.mediaFileId, c]))
    } catch {
      verdicts = {}
    }
  }

  async function probe(file: MediaFile) {
    probingId = file.id
    error = null
    try {
      const updated = await api.probe(file.id)
      files = files.map((f) => (f.id === file.id ? updated : f))
      // A freshly probed file now has an eligibility verdict — refresh the overlay.
      await loadVerdicts(selectedLibrary)
    } catch (err) {
      error = err instanceof Error ? err.message : 'Probe failed'
    } finally {
      probingId = null
    }
  }

  function resolution(file: MediaFile) {
    return file.width && file.height ? `${file.width}×${file.height}` : '—'
  }

  // A file is probed (and therefore has a verdict) when a candidate row exists for it.
  function isProbed(file: MediaFile): boolean {
    return verdicts[file.id] !== undefined
  }

  let eligibleCount = $derived(files.filter((f) => verdicts[f.id]?.eligible).length)
  let skippedCount = $derived(files.filter((f) => verdicts[f.id] && !verdicts[f.id].eligible).length)
  let unprobedCount = $derived(files.filter((f) => !isProbed(f)).length)
  let visible = $derived(
    show === 'eligible'
      ? files.filter((f) => verdicts[f.id]?.eligible)
      : show === 'skipped'
        ? files.filter((f) => verdicts[f.id] && !verdicts[f.id].eligible)
        : show === 'unprobed'
          ? files.filter((f) => !isProbed(f))
          : files,
  )
</script>

<header class="mb-6 flex flex-wrap items-end justify-between gap-4">
  <div>
    <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">Inventory</h1>
    <p class="text-sm text-slate-500 dark:text-slate-400">
      Discovered media and what the rules would do with it. Probing reads codec, resolution, and duration; the
      eligibility column shows whether a probed file would be optimised, and why. Nothing here modifies a file.
    </p>
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
  <div class="mb-4 flex flex-wrap gap-2">
    <button class="btn px-3 py-1 text-xs" class:btn-primary={show === 'all'} onclick={() => (show = 'all')}>
      All ({files.length})
    </button>
    <button class="btn px-3 py-1 text-xs" class:btn-primary={show === 'eligible'} onclick={() => (show = 'eligible')}>
      Eligible ({eligibleCount})
    </button>
    <button class="btn px-3 py-1 text-xs" class:btn-primary={show === 'skipped'} onclick={() => (show = 'skipped')}>
      Skipped ({skippedCount})
    </button>
    <button class="btn px-3 py-1 text-xs" class:btn-primary={show === 'unprobed'} onclick={() => (show = 'unprobed')}>
      Not probed ({unprobedCount})
    </button>
  </div>

  <div class="card overflow-x-auto">
    <table class="w-full text-sm">
      <thead class="border-b border-slate-200 text-left text-xs uppercase text-slate-500 dark:border-slate-700 dark:text-slate-400">
        <tr>
          <th class="px-4 py-3">Optimise?</th>
          <th class="px-4 py-3">File</th>
          <th class="hidden px-4 py-3 lg:table-cell">Kind</th>
          <th class="px-4 py-3">Size</th>
          <th class="hidden px-4 py-3 sm:table-cell">Video</th>
          <th class="hidden px-4 py-3 sm:table-cell">Resolution</th>
          <th class="hidden px-4 py-3 lg:table-cell">Audio</th>
          <th class="hidden px-4 py-3 md:table-cell">Subs</th>
          <th class="hidden px-4 py-3 md:table-cell">Duration</th>
          <th class="hidden px-4 py-3 xl:table-cell">Reason</th>
          <th class="px-4 py-3"></th>
        </tr>
      </thead>
      <tbody class="divide-y divide-slate-100 dark:divide-slate-800">
        {#each visible as file (file.id)}
          {@const verdict = verdicts[file.id]}
          <tr class="text-slate-700 dark:text-slate-300">
            <td class="px-4 py-2">
              {#if verdict?.eligible}
                <span class="badge bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400">Eligible</span>
              {:else if verdict}
                <span class="badge bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400">Skipped</span>
              {:else}
                <span class="badge bg-slate-100 text-slate-400 dark:bg-slate-800 dark:text-slate-500" title="Probe this file to evaluate it against the rules">Not probed</span>
              {/if}
            </td>
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
            <td class="hidden max-w-xs truncate px-4 py-2 text-xs text-slate-500 xl:table-cell dark:text-slate-400" title={verdict?.reason ?? ''}>{verdict?.reason ?? '—'}</td>
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
  <p class="mt-2 text-xs text-slate-400">{visible.length.toLocaleString()} of {files.length.toLocaleString()} files</p>
{:else}
  <div class="card p-8 text-center text-slate-500 dark:text-slate-400">
    No media here yet. Add a library and scan it from the Libraries page.
  </div>
{/if}
