<script lang="ts">
  import { api, type Candidate, type Library } from '../api'
  import { formatSize } from '../format'
  import Banner from '../components/Banner.svelte'

  let libraries = $state<Library[]>([])
  let candidates = $state<Candidate[]>([])
  let selectedLibrary = $state<number | 'all'>('all')
  let show = $state<'all' | 'eligible' | 'skipped'>('all')
  let error = $state<string | null>(null)
  let loading = $state(true)

  $effect(() => {
    void loadLibraries()
  })

  // Reload candidates whenever the library filter changes.
  $effect(() => {
    void loadCandidates(selectedLibrary)
  })

  async function loadLibraries() {
    try {
      libraries = await api.libraries()
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to load libraries'
    }
  }

  async function loadCandidates(filter: number | 'all') {
    loading = true
    error = null
    try {
      candidates = await api.candidates(filter === 'all' ? undefined : filter)
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to load candidates'
    } finally {
      loading = false
    }
  }

  let eligibleCount = $derived(candidates.filter((c) => c.eligible).length)
  let skippedCount = $derived(candidates.length - eligibleCount)
  let visible = $derived(
    show === 'eligible'
      ? candidates.filter((c) => c.eligible)
      : show === 'skipped'
        ? candidates.filter((c) => !c.eligible)
        : candidates,
  )
</script>

<header class="mb-6 flex flex-wrap items-end justify-between gap-4">
  <div>
    <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">Candidates</h1>
    <p class="text-sm text-slate-500 dark:text-slate-400">
      What each library's rule profile would optimise. This is a preview only — no FFmpeg runs and nothing is changed.
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
{:else if candidates.length > 0}
  <div class="mb-4 flex flex-wrap gap-2">
    <button class="btn px-3 py-1 text-xs" class:btn-primary={show === 'all'} onclick={() => (show = 'all')}>
      All ({candidates.length})
    </button>
    <button class="btn px-3 py-1 text-xs" class:btn-primary={show === 'eligible'} onclick={() => (show = 'eligible')}>
      Eligible ({eligibleCount})
    </button>
    <button class="btn px-3 py-1 text-xs" class:btn-primary={show === 'skipped'} onclick={() => (show = 'skipped')}>
      Skipped ({skippedCount})
    </button>
  </div>

  <div class="card overflow-x-auto">
    <table class="w-full text-sm">
      <thead class="border-b border-slate-200 text-left text-xs uppercase text-slate-500 dark:border-slate-700 dark:text-slate-400">
        <tr>
          <th class="px-4 py-3">Status</th>
          <th class="px-4 py-3">File</th>
          <th class="px-4 py-3">Size</th>
          <th class="px-4 py-3">Video</th>
          <th class="px-4 py-3">Profile</th>
          <th class="px-4 py-3">Reason</th>
        </tr>
      </thead>
      <tbody class="divide-y divide-slate-100 dark:divide-slate-800">
        {#each visible as candidate (candidate.mediaFileId)}
          <tr class="text-slate-700 dark:text-slate-300">
            <td class="px-4 py-2">
              {#if candidate.eligible}
                <span class="badge bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400">Eligible</span>
              {:else}
                <span class="badge bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400">Skipped</span>
              {/if}
            </td>
            <td class="max-w-xs truncate px-4 py-2 font-mono text-xs" title={candidate.relativePath}>{candidate.relativePath}</td>
            <td class="px-4 py-2">{formatSize(candidate.sizeBytes)}</td>
            <td class="px-4 py-2">
              {candidate.videoCodec ?? '—'}{#if candidate.isHdr}<span class="badge ml-1 bg-amber-100 text-amber-700 dark:bg-amber-900/40 dark:text-amber-400">HDR</span>{/if}
            </td>
            <td class="px-4 py-2 text-xs">{candidate.profile}</td>
            <td class="px-4 py-2 text-xs text-slate-500 dark:text-slate-400">{candidate.reason}</td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
  <p class="mt-2 text-xs text-slate-400">{visible.length.toLocaleString()} of {candidates.length.toLocaleString()} probed files</p>
{:else}
  <div class="card p-8 text-center text-slate-500 dark:text-slate-400">
    No candidates yet. Probe some files on the Inventory page first — candidates are evaluated from probed media.
  </div>
{/if}
