<script lang="ts">
  import { formatSize } from '../format'
  import { i18n, t } from '../i18n/i18n.svelte'
  import type { DashboardState } from '../dashboard-state'

  let {
    state: queueState,
    freeDiskBytes = null,
    workRoot = '',
    maxConcurrent = null,
  }: {
    state: DashboardState | null
    freeDiskBytes?: number | null
    workRoot?: string
    maxConcurrent?: number | null
  } = $props()

  // Each state names itself in words as well as colour, so the bar never depends on hue alone.
  const LABEL: Record<DashboardState['kind'], () => string> = {
    encoding: () => i18n.m.dashboard.state_encoding,
    paused: () => i18n.m.dashboard.state_paused,
    blocked: () => i18n.m.dashboard.state_blocked,
    waiting: () => i18n.m.dashboard.state_waiting,
    idle: () => i18n.m.dashboard.state_idle,
    unexplained: () => i18n.m.dashboard.state_unexplained,
  }

  let tone = $derived(
    queueState?.severity === 'live'
      ? 'text-cyan-700 dark:text-cyan-400'
      : queueState?.severity === 'attention'
        ? 'text-red-600 dark:text-red-400'
        : 'text-slate-800 dark:text-slate-100',
  )

  let reason = $derived(queueState?.detail ?? null)
  let slots = $derived(
    maxConcurrent ? `${queueState?.running ?? 0} / ${maxConcurrent}` : `${queueState?.running ?? 0}`,
  )
</script>

<div class="card mb-4 flex flex-wrap items-stretch divide-slate-200 overflow-hidden p-0 dark:divide-slate-700 sm:divide-x">
  <div class="flex items-center gap-2.5 px-4 py-3">
    {#if queueState?.severity === 'live'}
      <span class="h-2 w-2 flex-none animate-pulse rounded-full bg-cyan-600 dark:bg-cyan-400" aria-hidden="true"></span>
    {:else if queueState?.severity === 'attention'}
      <span class="h-2 w-2 flex-none rounded-full bg-red-600 dark:bg-red-400" aria-hidden="true"></span>
    {:else}
      <span class="h-2 w-2 flex-none rounded-full bg-slate-300 dark:bg-slate-600" aria-hidden="true"></span>
    {/if}
    <span class="label mb-0">{i18n.m.dashboard.state}</span>
    <span class="font-mono text-sm font-medium {tone}">{queueState ? LABEL[queueState.kind]() : '—'}</span>
  </div>

  {#if reason}
    <div class="flex min-w-0 flex-1 items-center gap-2.5 px-4 py-3">
      <span class="label mb-0 flex-none">{i18n.m.dashboard.state_reason}</span>
      <span class="truncate text-sm text-slate-600 dark:text-slate-300" title={reason}>{reason}</span>
    </div>
  {/if}

  <div class="flex items-center gap-2.5 px-4 py-3">
    <span class="label mb-0">{i18n.m.dashboard.slots}</span>
    <span class="font-mono text-sm font-medium tabular-nums text-slate-800 dark:text-slate-100">{slots}</span>
  </div>

  <div class="flex items-center gap-2.5 px-4 py-3">
    <span class="label mb-0">{i18n.m.nav.queue}</span>
    <span class="font-mono text-sm font-medium tabular-nums text-slate-800 dark:text-slate-100">{(queueState?.queued ?? 0).toLocaleString()}</span>
  </div>

  {#if freeDiskBytes != null}
    <div class="flex items-center gap-2.5 px-4 py-3 {reason ? '' : 'sm:ml-auto'}">
      <span class="label mb-0">{t(i18n.m.dashboard.free_on, { path: workRoot || '/work' })}</span>
      <span class="font-mono text-sm font-medium tabular-nums text-slate-800 dark:text-slate-100">{formatSize(freeDiskBytes)}</span>
    </div>
  {/if}
</div>
