<script lang="ts">
  import { api, type Library, type QueueStatus } from '../api'
  import { formatSize } from '../format'
  import { i18n, t } from '../i18n/i18n.svelte'
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
      error = err instanceof Error ? err.message : i18n.m.schedule.error_load
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

  let autoOptimiseLibraries = $derived(libraries.filter((l) => l.autoEnqueueEnabled))
</script>

<header class="mb-6">
  <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">{i18n.m.nav.schedule}</h1>
  <p class="text-sm text-slate-500 dark:text-slate-400">
    {i18n.m.schedule.intro_before}<button
      class="text-cyan-600 hover:underline dark:text-cyan-400"
      onclick={() => router.go('/settings')}
    >{i18n.m.nav.settings}</button>{i18n.m.schedule.intro_between}<button
      class="text-cyan-600 hover:underline dark:text-cyan-400"
      onclick={() => router.go('/libraries')}
    >{i18n.m.nav.libraries}</button>{i18n.m.schedule.intro_after}
  </p>
</header>

{#if error}
  <Banner kind="error" class="mb-4">{error}</Banner>
{/if}

{#if loading}
  <div class="card p-8 text-center text-slate-400">{i18n.m.common.loading_short}</div>
{:else if queueStatus}
  <!-- Dispatch status -->
  <div class="card mb-5 p-5">
    <h2 class="mb-4 font-semibold text-slate-800 dark:text-slate-100">{i18n.m.schedule.dispatch_status}</h2>
    <dl class="grid gap-x-8 gap-y-4 text-sm sm:grid-cols-2 lg:grid-cols-4">
      <div>
        <dt class="text-xs font-medium uppercase tracking-wide text-slate-400">{i18n.m.nav.queue}</dt>
        <dd class="mt-1">
          {#if queueStatus.canStart}
            <span class="badge bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400">{i18n.m.schedule.queue_ready}</span>
          {:else}
            <span class="badge bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300">{i18n.m.schedule.queue_paused}</span>
          {/if}
        </dd>
      </div>
      <div>
        <dt class="text-xs font-medium uppercase tracking-wide text-slate-400">{i18n.m.schedule.running_jobs}</dt>
        <dd class="mt-1 text-slate-700 dark:text-slate-200">
          {queueStatus.runningJobs} / {queueStatus.maxConcurrentJobs}
        </dd>
      </div>
      <div>
        <dt class="text-xs font-medium uppercase tracking-wide text-slate-400">{i18n.m.schedule.scan_interval}</dt>
        <dd class="mt-1 text-slate-700 dark:text-slate-200">
          {t(i18n.m.schedule.every_hours, { hours: queueStatus.libraryScanIntervalHours })}
        </dd>
      </div>
      <div>
        <dt class="text-xs font-medium uppercase tracking-wide text-slate-400">{i18n.m.schedule.work_disk_free}</dt>
        <dd class="mt-1 text-slate-700 dark:text-slate-200">
          {queueStatus.freeDiskBytes === null ? i18n.m.common.unknown : formatSize(queueStatus.freeDiskBytes)}
        </dd>
      </div>
      {#if queueStatus.blockedReason}
        <div>
          <dt class="text-xs font-medium uppercase tracking-wide text-slate-400">{i18n.m.schedule.blocked_reason}</dt>
          <dd class="mt-1 text-amber-700 dark:text-amber-300">{queueStatus.blockedReason}</dd>
        </div>
      {/if}
    </dl>
  </div>

  <!-- Per-library auto-optimise -->
  <div class="card p-5">
    <h2 class="mb-1 font-semibold text-slate-800 dark:text-slate-100">{i18n.m.schedule.auto_windows_title}</h2>
    <p class="mb-4 text-xs text-slate-500 dark:text-slate-400">
      {i18n.m.schedule.auto_windows_desc}
    </p>

    {#if autoOptimiseLibraries.length > 0}
      <div class="overflow-x-auto">
        <table class="w-full text-sm">
          <thead class="border-b border-slate-200 text-left text-xs uppercase text-slate-500 dark:border-slate-700 dark:text-slate-400">
            <tr>
              <th class="pb-2 pr-6">{i18n.m.schedule.col_library}</th>
              <th class="pb-2 pr-6">{i18n.m.schedule.col_window}</th>
              <th class="pb-2 pr-6">{i18n.m.schedule.col_status}</th>
              <th class="pb-2 pr-6">{i18n.m.schedule.col_auto_replace}</th>
              <th class="pb-2">{i18n.m.schedule.col_last_enqueued}</th>
            </tr>
          </thead>
          <tbody class="divide-y divide-slate-100 dark:divide-slate-800">
            {#each autoOptimiseLibraries as lib (lib.id)}
              {@const active = inWindow(lib.autoEnqueueWindowStart, lib.autoEnqueueWindowEnd)}
              {@const overnight = isOvernightWindow(lib.autoEnqueueWindowStart, lib.autoEnqueueWindowEnd)}
              <tr class="text-slate-700 dark:text-slate-300">
                <td class="py-2 pr-6 font-medium">{lib.name}</td>
                <td class="py-2 pr-6 font-mono text-xs">
                  {lib.autoEnqueueWindowStart} → {lib.autoEnqueueWindowEnd}
                  {#if overnight}<span class="ml-1 font-sans text-slate-400">{i18n.m.schedule.overnight}</span>{/if}
                </td>
                <td class="py-2 pr-6">
                  {#if active}
                    <span class="badge bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400">{i18n.m.schedule.in_window}</span>
                  {:else}
                    <span class="badge bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-400">{i18n.m.schedule.idle}</span>
                  {/if}
                </td>
                <td class="py-2 pr-6 text-xs">
                  {lib.autoReplace ? i18n.m.schedule.when_verified : i18n.m.common.off}
                </td>
                <td class="py-2 text-xs text-slate-500 dark:text-slate-400">
                  {lib.lastAutoEnqueueAt
                    ? new Date(lib.lastAutoEnqueueAt).toLocaleString()
                    : i18n.m.common.never}
                </td>
              </tr>
            {/each}
          </tbody>
        </table>
      </div>
    {:else}
      <p class="text-sm text-slate-400">
        {i18n.m.schedule.none_before}<button
          class="text-cyan-600 hover:underline dark:text-cyan-400"
          onclick={() => router.go('/libraries')}
        >{i18n.m.nav.libraries}</button>{i18n.m.schedule.none_after}
      </p>
    {/if}
  </div>
{/if}
