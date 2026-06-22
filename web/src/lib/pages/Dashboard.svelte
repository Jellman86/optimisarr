<script lang="ts">
  import { api, type Health, type ToolCheck, type Stats } from '../api'
  import { formatSize } from '../format'
  import { router } from '../stores/ui.svelte'
  import Banner from '../components/Banner.svelte'
  import Icon from '../components/Icon.svelte'

  let health = $state<Health | null>(null)
  let tools = $state<ToolCheck[]>([])
  let stats = $state<Stats | null>(null)
  let error = $state<string | null>(null)

  $effect(() => {
    void load()
    // Queue/quarantine figures change as work runs; refresh periodically.
    const timer = setInterval(load, 15000)
    return () => clearInterval(timer)
  })

  async function load() {
    error = null
    try {
      ;[health, tools, stats] = await Promise.all([api.health(), api.tools(), api.stats()])
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to load status'
    }
  }

  let toolsReady = $derived(tools.length > 0 && tools.every((t) => t.available))
  let healthy = $derived(health?.status === 'healthy' && toolsReady)
  let activeWork = $derived((stats?.running ?? 0) + (stats?.queued ?? 0))
</script>

<header class="mb-6">
  <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">Dashboard</h1>
  <p class="text-sm text-slate-500 dark:text-slate-400">What Optimisarr has saved, and what's in flight.</p>
</header>

{#if error}
  <Banner kind="error" class="mb-6">{error}</Banner>
{/if}

<!-- Headline outcome: realised space saved across every file whose optimised version is in place. -->
<div class="card mb-4 p-6">
  <div class="label">Total space saved</div>
  {#if stats}
    <div class="mt-1 text-4xl font-bold tabular-nums text-emerald-600 dark:text-emerald-400">{formatSize(stats.bytesSaved)}</div>
    {#if stats.filesOptimised > 0}
      <div class="mt-1 text-sm text-slate-500 dark:text-slate-400">
        {stats.filesOptimised.toLocaleString()} file{stats.filesOptimised === 1 ? '' : 's'} optimised ·
        {formatSize(stats.originalBytes)} → {formatSize(stats.optimisedBytes)}
        ({Math.round(stats.averageSavingPercent)}% smaller on average)
      </div>
    {:else}
      <div class="mt-1 text-sm text-slate-500 dark:text-slate-400">
        Nothing optimised yet. Add a library and queue some work to start saving space.
        <button class="text-cyan-600 hover:underline dark:text-cyan-400" onclick={() => router.go('/libraries')}>Add a library →</button>
      </div>
    {/if}
  {:else}
    <div class="mt-1 text-4xl font-bold text-slate-300 dark:text-slate-700">—</div>
  {/if}
</div>

<div class="mb-4 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
  <!-- In flight -->
  <button class="card p-4 text-left transition hover:border-cyan-300 dark:hover:border-cyan-800" onclick={() => router.go('/queue')}>
    <div class="label">Queue</div>
    <div class="text-2xl font-semibold tabular-nums text-slate-800 dark:text-slate-100">{(stats?.running ?? 0).toLocaleString()} <span class="text-base font-normal text-slate-400">running</span></div>
    <div class="text-sm text-slate-400">
      {(stats?.queued ?? 0).toLocaleString()} queued{#if (stats?.failed ?? 0) > 0} · <span class="text-red-600 dark:text-red-400">{stats?.failed} failed</span>{/if}
    </div>
  </button>

  <!-- Awaiting review (quarantine) -->
  <button class="card p-4 text-left transition hover:border-cyan-300 dark:hover:border-cyan-800" onclick={() => router.go('/quarantine')}>
    <div class="label">Awaiting review</div>
    <div class="text-2xl font-semibold tabular-nums text-slate-800 dark:text-slate-100">{(stats?.inQuarantine ?? 0).toLocaleString()}</div>
    <div class="text-sm text-slate-400">
      {#if (stats?.inQuarantine ?? 0) > 0}reclaim {formatSize(stats?.quarantineReclaimableBytes ?? 0)} on approve{:else}in quarantine{/if}
    </div>
  </button>

  <!-- Ready to replace -->
  <button class="card p-4 text-left transition hover:border-cyan-300 dark:hover:border-cyan-800" onclick={() => router.go('/queue')}>
    <div class="label">Ready to replace</div>
    <div class="text-2xl font-semibold tabular-nums text-slate-800 dark:text-slate-100">{(stats?.readyToReplace ?? 0).toLocaleString()}</div>
    <div class="text-sm text-slate-400">verified, awaiting replace</div>
  </button>

  <!-- Libraries -->
  <button class="card p-4 text-left transition hover:border-cyan-300 dark:hover:border-cyan-800" onclick={() => router.go('/libraries')}>
    <div class="label">Libraries</div>
    <div class="text-2xl font-semibold tabular-nums text-slate-800 dark:text-slate-100">{(stats?.libraries ?? 0).toLocaleString()}</div>
    <div class="text-sm text-slate-400">
      {(stats?.enabledLibraries ?? 0).toLocaleString()} enabled · {(stats?.discoveredFiles ?? 0).toLocaleString()} files
    </div>
  </button>
</div>

<!-- System health -->
<div class="card p-4">
  <div class="flex flex-wrap items-center justify-between gap-3">
    <div class="flex items-center gap-3">
      <span class="flex h-9 w-9 items-center justify-center rounded-full {healthy ? 'bg-emerald-100 text-emerald-600 dark:bg-emerald-900/40 dark:text-emerald-400' : 'bg-amber-100 text-amber-600 dark:bg-amber-950 dark:text-amber-300'}">
        <Icon name={healthy ? 'check' : 'warning'} class="h-5 w-5" />
      </span>
      <div>
        <div class="font-semibold text-slate-800 dark:text-slate-100">{healthy ? 'All systems healthy' : 'Needs attention'}</div>
        <div class="text-sm text-slate-400">
          Service {health?.status ?? 'unknown'} · media tools {toolsReady ? 'ready' : 'not detected'}{#if activeWork > 0} · {activeWork.toLocaleString()} job{activeWork === 1 ? '' : 's'} in flight{/if}
        </div>
      </div>
    </div>
    <button class="btn" onclick={() => router.go('/settings')}>Settings &amp; tools</button>
  </div>
</div>
