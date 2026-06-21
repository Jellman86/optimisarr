<script lang="ts">
  import { api, type Candidate, type Library, type MediaFile } from '../api'
  import { formatSize, formatDuration } from '../format'
  import Banner from '../components/Banner.svelte'
  import BottomSheet from '../components/BottomSheet.svelte'
  import PreviewCompare from '../components/PreviewCompare.svelte'

  let libraries = $state<Library[]>([])
  let files = $state<MediaFile[]>([])
  // Eligibility verdict per file (only probed files have one), keyed by media-file id.
  let verdicts = $state<Record<number, Candidate>>({})
  let selectedLibrary = $state<number | 'all'>('all')
  let show = $state<'all' | 'eligible' | 'skipped' | 'unprobed'>('all')
  let page = $state(1)
  let selectedId = $state<number | null>(null)
  // Whether the detail sheet is showing its full content (true) or just the header strip (false).
  let sheetExpanded = $state(true)
  // The file open in the original-vs-encoded preview, if any.
  let previewing = $state<MediaFile | null>(null)
  let error = $state<string | null>(null)
  let probingId = $state<number | null>(null)
  let loading = $state(true)

  // The detail sheet's measured height, so the table can subtract it to stay fully scrollable.
  let sheetHeight = $state(0)

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
    selectedId = null
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

  function selectLibrary(event: Event) {
    selectedLibrary =
      (event.currentTarget as HTMLSelectElement).value === 'all'
        ? 'all'
        : Number((event.currentTarget as HTMLSelectElement).value)
    page = 1
    selectedId = null
  }

  function selectFilter(value: typeof show) {
    show = value
    page = 1
    selectedId = null
  }

  function goToPage(nextPage: number) {
    page = Math.max(1, Math.min(nextPage, pageCount))
    selectedId = null
  }

  // Toggle: clicking the active row again dismisses the detail sheet.
  // Opening a new row always starts expanded so the details are immediately visible.
  function selectRow(id: number) {
    if (selectedId === id) {
      selectedId = null
    } else {
      selectedId = id
      sheetExpanded = true
    }
  }

  function dismissSheet() {
    selectedId = null
  }

  function onKeydown(e: KeyboardEvent) {
    if (e.key === 'Escape' && selectedId !== null) dismissSheet()
  }

  let eligibleCount = $derived(files.filter((f) => verdicts[f.id]?.eligible).length)
  let skippedCount = $derived(files.filter((f) => verdicts[f.id] && !verdicts[f.id].eligible).length)
  let unprobedCount = $derived(files.filter((f) => !isProbed(f)).length)
  let filtered = $derived(
    show === 'eligible'
      ? files.filter((f) => verdicts[f.id]?.eligible)
      : show === 'skipped'
        ? files.filter((f) => verdicts[f.id] && !verdicts[f.id].eligible)
        : show === 'unprobed'
          ? files.filter((f) => !isProbed(f))
          : files,
  )
  const pageSize = 50
  let pageCount = $derived(Math.max(1, Math.ceil(filtered.length / pageSize)))
  let pageStart = $derived((Math.min(page, pageCount) - 1) * pageSize)
  let paged = $derived(filtered.slice(pageStart, pageStart + pageSize))
  // No auto-selection: selectedFile is null until the user clicks a row.
  let selectedFile = $derived(
    selectedId !== null ? (paged.find((file) => file.id === selectedId) ?? null) : null,
  )
  let selectedVerdict = $derived(selectedFile ? verdicts[selectedFile.id] : undefined)
</script>

<svelte:window onkeydown={onKeydown} />

<header class="mb-4 flex flex-wrap items-end justify-between gap-4">
  <div>
    <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">Inventory</h1>
    <p class="text-sm text-slate-500 dark:text-slate-400">
      Discovered media and what the rules would do with it. Click a row to inspect it.
    </p>
  </div>
  <div>
    <label class="label" for="lib-filter">Library</label>
    <select id="lib-filter" class="input min-w-48" value={selectedLibrary} onchange={selectLibrary}>
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
  <!-- Filter tabs and pagination on the same row so both are always visible. -->
  <div class="mb-3 flex flex-wrap items-center justify-between gap-2">
    <div class="flex flex-wrap gap-2">
      <button
        class="btn px-3 py-1 text-xs"
        class:btn-primary={show === 'all'}
        onclick={() => selectFilter('all')}
      >
        All ({files.length.toLocaleString()})
      </button>
      <button
        class="btn px-3 py-1 text-xs"
        class:btn-primary={show === 'eligible'}
        onclick={() => selectFilter('eligible')}
      >
        Eligible ({eligibleCount.toLocaleString()})
      </button>
      <button
        class="btn px-3 py-1 text-xs"
        class:btn-primary={show === 'skipped'}
        onclick={() => selectFilter('skipped')}
      >
        Skipped ({skippedCount.toLocaleString()})
      </button>
      <button
        class="btn px-3 py-1 text-xs"
        class:btn-primary={show === 'unprobed'}
        onclick={() => selectFilter('unprobed')}
      >
        Not probed ({unprobedCount.toLocaleString()})
      </button>
    </div>

    <!-- Compact pagination: always visible above the table. -->
    <div class="flex items-center gap-2 text-xs text-slate-400">
      <span>
        {filtered.length === 0 ? '0' : (pageStart + 1).toLocaleString()}–{Math.min(
          pageStart + pageSize,
          filtered.length,
        ).toLocaleString()} of {filtered.length.toLocaleString()}
      </span>
      <button
        class="btn px-2 py-1 text-xs"
        onclick={() => goToPage(page - 1)}
        disabled={page <= 1}
        aria-label="Previous page"
      >
        ‹
      </button>
      <span>p.{Math.min(page, pageCount)}/{pageCount}</span>
      <button
        class="btn px-2 py-1 text-xs"
        onclick={() => goToPage(page + 1)}
        disabled={page >= pageCount}
        aria-label="Next page"
      >
        ›
      </button>
    </div>
  </div>

  <!-- Table scrolls within a fixed-height container so the page itself never needs to scroll.
       The sticky thead keeps column headers visible as the body scrolls.
       When the detail sheet is open its measured height is subtracted so the table shrinks to
       keep all rows reachable above the panel. -->
  <div class="card overflow-hidden">
    <div
      class="overflow-y-auto"
      style="max-height: calc(100dvh - 11rem - {selectedFile ? `${sheetHeight}px` : '0px'}); transition: max-height 0.3s ease-out;"
    >
      <table class="w-full text-sm">
        <thead
          class="sticky top-0 z-10 border-b border-slate-200 bg-white text-left text-xs uppercase text-slate-500 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-400"
        >
          <tr>
            <th class="px-4 py-3">Optimise?</th>
            <th class="px-4 py-3">File</th>
            <th class="hidden px-4 py-3 lg:table-cell">Kind</th>
            <th class="px-4 py-3">Size</th>
            <th class="hidden px-4 py-3 sm:table-cell">Video</th>
            <th class="hidden px-4 py-3 sm:table-cell">Resolution</th>
          </tr>
        </thead>
        <tbody class="divide-y divide-slate-100 dark:divide-slate-800">
          {#each paged as file (file.id)}
            {@const verdict = verdicts[file.id]}
            <tr
              class="cursor-pointer text-slate-700 hover:bg-slate-50 dark:text-slate-300 dark:hover:bg-slate-800/50 {selectedFile?.id ===
              file.id
                ? 'bg-sky-50 dark:bg-sky-950/30'
                : ''}"
              onclick={() => selectRow(file.id)}
            >
              <td class="px-4 py-2">
                {#if verdict?.eligible}
                  <span
                    class="badge bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400"
                    >Eligible</span
                  >
                {:else if verdict}
                  <span class="badge bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400"
                    >Skipped</span
                  >
                {:else}
                  <span
                    class="badge bg-slate-100 text-slate-400 dark:bg-slate-800 dark:text-slate-500"
                    title="Probe this file to evaluate it against the rules">Not probed</span
                  >
                {/if}
              </td>
              <td
                class="max-w-[55vw] truncate px-4 py-2 font-mono text-xs sm:max-w-xs"
                title={file.relativePath}>{file.relativePath}</td
              >
              <td class="hidden px-4 py-2 lg:table-cell">
                {#if file.mediaKind && file.mediaKind !== 'Unknown'}
                  <span class="badge bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300"
                    >{file.mediaKind}</span
                  >
                {:else}
                  <span class="text-slate-400">—</span>
                {/if}
              </td>
              <td class="px-4 py-2">{formatSize(file.sizeBytes)}</td>
              <td class="hidden px-4 py-2 sm:table-cell">{file.videoCodec ?? '—'}</td>
              <td class="hidden px-4 py-2 sm:table-cell">{resolution(file)}</td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
  </div>
{:else}
  <div class="card p-8 text-center text-slate-500 dark:text-slate-400">
    No media here yet. Add a library and scan it from the Libraries page.
  </div>
{/if}

<!-- Detail bottom sheet: slides into view on row selection. -->
<BottomSheet open={selectedFile !== null} bind:expanded={sheetExpanded} bind:height={sheetHeight} onclose={dismissSheet}>
  {#snippet header()}
    <p class="break-all font-mono text-xs leading-relaxed text-slate-700 dark:text-slate-200">
      {selectedFile?.relativePath ?? ''}
    </p>
  {/snippet}
  {#snippet children()}
    {#if selectedFile}
        <dl class="grid gap-x-8 gap-y-3 text-sm sm:grid-cols-2 xl:grid-cols-4">
          <div class="flex justify-between gap-4">
            <dt class="text-slate-500">Status</dt>
            <dd>{selectedFile.status}</dd>
          </div>
          <div class="flex justify-between gap-4">
            <dt class="text-slate-500">Size</dt>
            <dd>{formatSize(selectedFile.sizeBytes)}</dd>
          </div>
          <div class="flex justify-between gap-4">
            <dt class="text-slate-500">Container</dt>
            <dd>{selectedFile.container ?? '—'}</dd>
          </div>
          <div class="flex justify-between gap-4">
            <dt class="text-slate-500">Video</dt>
            <dd>{selectedFile.videoCodec ?? '—'} {resolution(selectedFile)}</dd>
          </div>
          <div class="flex justify-between gap-4">
            <dt class="text-slate-500">Audio</dt>
            <dd class="text-right">
              {selectedFile.audioCodecs ?? '—'}{selectedFile.audioTrackCount
                ? ` · ${selectedFile.audioTrackCount} tracks`
                : ''}
            </dd>
          </div>
          <div class="flex justify-between gap-4">
            <dt class="text-slate-500">Subtitles</dt>
            <dd>{selectedFile.subtitleTrackCount ?? '—'}</dd>
          </div>
          <div class="flex justify-between gap-4">
            <dt class="text-slate-500">Duration</dt>
            <dd>{formatDuration(selectedFile.durationSeconds)}</dd>
          </div>
        </dl>

        <div class="mt-4 border-t border-slate-100 pt-4 text-sm dark:border-slate-800">
          <p class="text-xs font-semibold uppercase tracking-wide text-slate-400">Rule verdict</p>
          <p class="mt-2 text-slate-600 dark:text-slate-300">
            {selectedVerdict?.reason ?? 'Probe this file to evaluate it against the library rules.'}
          </p>
        </div>

        {#if selectedFile.probeError}
          <p class="mt-3 text-xs text-red-600" title={selectedFile.probeError}>
            Probe failed: {selectedFile.probeError}
          </p>
        {/if}

        <div class="mt-4 flex flex-wrap gap-2">
          <button
            class="btn px-3 py-1 text-xs"
            onclick={() => { if (selectedFile) probe(selectedFile) }}
            disabled={probingId === selectedFile.id}
          >
            {probingId === selectedFile.id
              ? 'Probing…'
              : selectedFile.status === 'Discovered'
                ? 'Probe'
                : 'Re-probe'}
          </button>
          {#if selectedVerdict?.eligible}
            <button class="btn px-3 py-1 text-xs" onclick={() => (previewing = selectedFile)}>
              Preview
            </button>
          {/if}
        </div>
    {/if}
  {/snippet}
</BottomSheet>

{#if previewing}
  <PreviewCompare
    mediaFileId={previewing.id}
    mediaKind={previewing.mediaKind ?? 'Video'}
    relativePath={previewing.relativePath}
    onClose={() => (previewing = null)}
  />
{/if}
