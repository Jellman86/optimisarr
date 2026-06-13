<script lang="ts">
  import { api, type Candidate, type Library } from '../api'
  import Banner from '../components/Banner.svelte'
  import CandidateTable from '../components/CandidateTable.svelte'

  let libraries = $state<Library[]>([])
  let candidates = $state<Candidate[]>([])
  let selectedLibrary = $state<number | 'all'>('all')
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
{:else}
  <CandidateTable {candidates} />
{/if}
