<script lang="ts">
  import { api, type Health, type ToolCheck, type Library } from '../api'
  import { router } from '../stores/ui.svelte'

  let health = $state<Health | null>(null)
  let tools = $state<ToolCheck[]>([])
  let libraries = $state<Library[]>([])
  let error = $state<string | null>(null)

  const stages = ['Discover', 'Probe', 'Plan', 'Transcode', 'Verify', 'Quarantine', 'Replace']

  $effect(() => {
    void load()
  })

  async function load() {
    error = null
    try {
      ;[health, tools, libraries] = await Promise.all([api.health(), api.tools(), api.libraries()])
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to load status'
    }
  }

  let totalFiles = $derived(libraries.reduce((sum, l) => sum + l.fileCount, 0))
  let toolsReady = $derived(tools.length > 0 && tools.every((t) => t.available))
</script>

<header class="mb-6">
  <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">Dashboard</h1>
  <p class="text-sm text-slate-500 dark:text-slate-400">Service health and library overview.</p>
</header>

{#if error}
  <div class="card mb-6 border-red-300 p-4 text-red-700 dark:border-red-800 dark:text-red-400">{error}</div>
{/if}

<div class="mb-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
  <div class="card p-4">
    <div class="label">Service</div>
    <div class="text-lg font-semibold text-slate-800 dark:text-slate-100">{health?.service ?? 'optimisarr'}</div>
    <div class="text-sm {health?.status === 'healthy' ? 'text-emerald-600' : 'text-slate-400'}">{health?.status ?? 'unknown'}</div>
  </div>
  <div class="card p-4">
    <div class="label">Media tools</div>
    <div class="text-lg font-semibold text-slate-800 dark:text-slate-100">{toolsReady ? 'Ready' : 'Check'}</div>
    <div class="text-sm text-slate-400">FFmpeg &amp; ffprobe</div>
  </div>
  <div class="card p-4">
    <div class="label">Libraries</div>
    <div class="text-lg font-semibold text-slate-800 dark:text-slate-100">{libraries.length}</div>
    <div class="text-sm text-slate-400">{libraries.filter((l) => l.enabled).length} enabled</div>
  </div>
  <div class="card p-4">
    <div class="label">Discovered files</div>
    <div class="text-lg font-semibold text-slate-800 dark:text-slate-100">{totalFiles.toLocaleString()}</div>
    <div class="text-sm text-slate-400">across all libraries</div>
  </div>
</div>

<div class="card mb-6 p-5">
  <div class="mb-3 flex items-center justify-between">
    <h2 class="font-semibold text-slate-800 dark:text-slate-100">Pipeline</h2>
    <button class="btn" onclick={() => router.go('/libraries')}>Manage libraries</button>
  </div>
  <ol class="flex flex-wrap gap-2">
    {#each stages as stage, index}
      <li
        class="flex items-center gap-2 rounded-lg border px-3 py-2 text-sm {index < 2
          ? 'border-emerald-300 bg-emerald-50 text-emerald-800 dark:border-emerald-800 dark:bg-emerald-950 dark:text-emerald-300'
          : 'border-slate-200 text-slate-500 dark:border-slate-700 dark:text-slate-400'}"
      >
        <span class="flex h-6 w-6 items-center justify-center rounded-full bg-white text-xs font-semibold dark:bg-slate-800">{index + 1}</span>
        {stage}
      </li>
    {/each}
  </ol>
  <p class="mt-3 text-xs text-slate-400">Only Discover and Probe are active today; later stages arrive with the queue and verification phases.</p>
</div>
