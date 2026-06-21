<script lang="ts">
  import { api, type Library, type QueueStatus } from '../api'
  import { formatSize } from '../format'
  import { router } from '../stores/ui.svelte'
  import Banner from '../components/Banner.svelte'

  let queueStatus = $state<QueueStatus | null>(null)
  let libraries = $state<Library[]>([])
  let error = $state<string | null>(null)
  let loading = $state(true)

  $effect(() => {
    void load()
  })

  async function load() {
    loading = true
    error = null
    try {
      ;[queueStatus, libraries] = await Promise.all([api.queueStatus(), api.libraries()])
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to load schedule'
    } finally {
      loading = false
    }
  }

  // Returns true when the current local time falls inside start→end.
  // Handles overnight windows (end < start, e.g. 22:00→06:00).
  // A 00:00→00:00 window means "always on" and is treated as all-day.
  function inWindow(start: string, end: string): boolean {
    const [sh, sm] = start.split(':').map(Number)
    const [eh, em] = end.split(':').map(Number)
    const startMin = sh * 60 + sm
    const endMin = eh * 60 + em
    if (startMin === endMin) return true
    const now = new Date()
    const nowMin = now.getHours() * 60 + now.getMinutes()
    return startMin < endMin
      ? nowMin >= startMin && nowMin < endMin
      : nowMin >= startMin || nowMin < endMin
  }

  function isOvernightWindow(start: string, end: string): boolean {
    const [sh, sm] = start.split(':').map(Number)
    const [eh, em] = end.split(':').map(Number)
    return sh * 60 + sm > eh * 60 + em
  }

  let autoEnqueueLibraries = $derived(libraries.filter((l) => l.autoEnqueueEnabled))
</script>

<header class="mb-6">
  <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">Schedule</h1>
  <p class="text-sm text-slate-500 dark:text-slate-400">
    When Optimisarr runs jobs. Edit the global window and per-library auto-enqueue in
    <button
      class="text-cyan-600 hover:underline dark:text-cyan-400"
      onclick={() => router.go('/settings')}
    >Settings</button>.
  </p>
</header>

{#if error}
  <Banner kind="error" class="mb-4">{error}</Banner>
{/if}

{#if loading}
  <div class="card p-8 text-center text-slate-400">Loading…</div>
{:else if queueStatus}
  <!-- Dispatch status -->
  <div class="card mb-5 p-5">
    <h2 class="mb-4 font-semibold text-slate-800 dark:text-slate-100">Dispatch status</h2>
    <dl class="grid gap-x-8 gap-y-4 text-sm sm:grid-cols-2 lg:grid-cols-4">
      <div>
        <dt class="text-xs font-medium uppercase tracking-wide text-slate-400">Queue</dt>
        <dd class="mt-1">
          {#if queueStatus.canStart}
            <span class="badge bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400">Ready</span>
          {:else}
            <span class="badge bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300">Paused</span>
          {/if}
        </dd>
      </div>
      <div>
        <dt class="text-xs font-medium uppercase tracking-wide text-slate-400">Running jobs</dt>
        <dd class="mt-1 text-slate-700 dark:text-slate-200">
          {queueStatus.runningJobs} / {queueStatus.maxConcurrentJobs}
        </dd>
      </div>
      <div>
        <dt class="text-xs font-medium uppercase tracking-wide text-slate-400">Work disk free</dt>
        <dd class="mt-1 text-slate-700 dark:text-slate-200">
          {queueStatus.freeDiskBytes === null ? 'Unknown' : formatSize(queueStatus.freeDiskBytes)}
        </dd>
      </div>
      {#if queueStatus.blockedReason}
        <div>
          <dt class="text-xs font-medium uppercase tracking-wide text-slate-400">Blocked reason</dt>
          <dd class="mt-1 text-amber-700 dark:text-amber-300">{queueStatus.blockedReason}</dd>
        </div>
      {/if}
    </dl>
  </div>

  <!-- Processing window -->
  <div class="card mb-5 p-5">
    <h2 class="mb-4 font-semibold text-slate-800 dark:text-slate-100">Processing window</h2>
    {#if queueStatus.scheduleEnabled}
      {@const active = inWindow(queueStatus.scheduleWindowStart, queueStatus.scheduleWindowEnd)}
      {@const overnight = isOvernightWindow(queueStatus.scheduleWindowStart, queueStatus.scheduleWindowEnd)}
      <div class="flex flex-wrap items-center gap-6 text-sm">
        <div>
          <span class="text-slate-500">Window</span>
          <span class="ml-2 font-mono text-slate-700 dark:text-slate-200">
            {queueStatus.scheduleWindowStart} → {queueStatus.scheduleWindowEnd}
            {#if overnight}<span class="ml-1 text-xs text-slate-400">(overnight)</span>{/if}
          </span>
        </div>
        <div>
          {#if active}
            <span class="badge bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400">In window now</span>
          {:else}
            <span class="badge bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300">Outside window</span>
          {/if}
        </div>
      </div>
      <p class="mt-3 text-xs text-slate-500 dark:text-slate-400">
        Running jobs are never interrupted. Outside the window, new jobs wait until it reopens.
      </p>
    {:else}
      <p class="text-sm text-slate-500 dark:text-slate-400">
        No processing window is configured — jobs run at any time. Enable one in
        <button
          class="text-cyan-600 hover:underline dark:text-cyan-400"
          onclick={() => router.go('/settings')}
        >Settings</button>.
      </p>
    {/if}
  </div>

  <!-- Per-library auto-enqueue -->
  <div class="card p-5">
    <h2 class="mb-1 font-semibold text-slate-800 dark:text-slate-100">Auto-enqueue</h2>
    <p class="mb-4 text-xs text-slate-500 dark:text-slate-400">
      A library can scan and enqueue itself once per day inside its own window, then let the
      global dispatch window decide when those jobs actually run.
    </p>

    {#if autoEnqueueLibraries.length > 0}
      <div class="overflow-x-auto">
        <table class="w-full text-sm">
          <thead class="border-b border-slate-200 text-left text-xs uppercase text-slate-500 dark:border-slate-700 dark:text-slate-400">
            <tr>
              <th class="pb-2 pr-6">Library</th>
              <th class="pb-2 pr-6">Enqueue window</th>
              <th class="pb-2">Last ran</th>
            </tr>
          </thead>
          <tbody class="divide-y divide-slate-100 dark:divide-slate-800">
            {#each autoEnqueueLibraries as lib (lib.id)}
              <tr class="text-slate-700 dark:text-slate-300">
                <td class="py-2 pr-6 font-medium">{lib.name}</td>
                <td class="py-2 pr-6 font-mono text-xs">
                  {lib.autoEnqueueWindowStart} → {lib.autoEnqueueWindowEnd}
                </td>
                <td class="py-2 text-xs text-slate-500 dark:text-slate-400">
                  {lib.lastAutoEnqueueAt
                    ? new Date(lib.lastAutoEnqueueAt).toLocaleString()
                    : 'Never'}
                </td>
              </tr>
            {/each}
          </tbody>
        </table>
      </div>
    {:else}
      <p class="text-sm text-slate-400">
        No libraries have auto-enqueue enabled. Open a library from the
        <button
          class="text-cyan-600 hover:underline dark:text-cyan-400"
          onclick={() => router.go('/libraries')}
        >Libraries</button>
        page and enable it under Advanced settings.
      </p>
    {/if}
  </div>
{/if}
