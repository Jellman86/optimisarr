<script lang="ts">
  import type { Stats } from '../api'
  import { formatSize } from '../format'
  import { i18n, plural, t } from '../i18n/i18n.svelte'
  import { remainingWork } from '../dashboard-state'
  import Icon from './Icon.svelte'

  let {
    stats,
    healthy,
    healthDetail,
    confirmingReset = $bindable(false),
    resetting = false,
    onreset,
  }: {
    stats: Stats | null
    healthy: boolean
    healthDetail: string
    confirmingReset?: boolean
    resetting?: boolean
    onreset: () => void
  } = $props()

  // A count, not a projected size. See remainingWork() for why there is no estimated
  // bytes-still-to-save figure here.
  let remaining = $derived(stats ? remainingWork(stats) : null)
</script>

<div class="card grid grid-cols-1 divide-y divide-slate-200 dark:divide-slate-700 sm:grid-cols-2 sm:divide-y-0 lg:grid-cols-4 lg:divide-x">
  <div class="p-4">
    <div class="flex items-start justify-between gap-2">
      <span class="label mb-0">{i18n.m.dashboard.total_saved}</span>
      {#if stats && stats.filesOptimised > 0}
        {#if confirmingReset}
          <span class="flex items-center gap-1">
            <button class="btn btn-danger px-2 py-0.5 text-[10px]" onclick={onreset} disabled={resetting}>{resetting ? i18n.m.dashboard.resetting : i18n.m.dashboard.reset}</button>
            <button class="btn px-2 py-0.5 text-[10px]" onclick={() => (confirmingReset = false)} disabled={resetting}>{i18n.m.common.cancel}</button>
          </span>
        {:else}
          <button
            class="text-slate-400 transition hover:text-slate-600 dark:hover:text-slate-200"
            title={i18n.m.dashboard.reset_title}
            aria-label={i18n.m.dashboard.reset_title}
            onclick={() => (confirmingReset = true)}
          ><Icon name="trash" class="h-3.5 w-3.5" /></button>
        {/if}
      {/if}
    </div>
    <div class="mt-1.5 font-mono text-xl font-semibold tabular-nums text-emerald-700 dark:text-emerald-400">
      {stats ? formatSize(stats.bytesSaved) : '—'}
    </div>
    <div class="mt-1 text-xs text-slate-500 dark:text-slate-400">
      {#if stats && stats.filesOptimised > 0}
        {t(i18n.m.dashboard.saved_detail, {
          count: stats.filesOptimised.toLocaleString(),
          percent: Math.round(stats.averageSavingPercent),
        })}
      {:else}
        {i18n.m.dashboard.empty_short}
      {/if}
    </div>
  </div>

  <div class="p-4">
    <div class="label mb-0">{i18n.m.dashboard.remaining}</div>
    <div class="mt-1.5 font-mono text-xl font-semibold tabular-nums text-slate-800 dark:text-slate-100">
      {remaining ? remaining.files.toLocaleString() : '—'}
    </div>
    <div class="mt-1 text-xs text-slate-500 dark:text-slate-400">
      {#if remaining && remaining.files > 0}
        {t(i18n.m.dashboard.remaining_detail, { queued: remaining.queued.toLocaleString() })}
      {:else}
        {i18n.m.dashboard.remaining_none}
      {/if}
    </div>
  </div>

  <div class="p-4">
    <div class="label mb-0">{i18n.m.nav.libraries}</div>
    <div class="mt-1.5 font-mono text-xl font-semibold tabular-nums text-slate-800 dark:text-slate-100">
      {(stats?.libraries ?? 0).toLocaleString()}
    </div>
    <div class="mt-1 text-xs text-slate-500 dark:text-slate-400">
      {t(i18n.m.dashboard.libraries_detail, {
        enabled: (stats?.enabledLibraries ?? 0).toLocaleString(),
        files: (stats?.discoveredFiles ?? 0).toLocaleString(),
      })}
    </div>
  </div>

  <!-- Health earns attention only when it changes. A status that is true almost always does
       not deserve a card of its own; it deserves a cell that goes red and says what is missing. -->
  <div class="p-4">
    <div class="label mb-0">{i18n.m.dashboard.health}</div>
    <div class="mt-1.5 flex items-center gap-2 font-mono text-xl font-semibold {healthy ? 'text-emerald-700 dark:text-emerald-400' : 'text-amber-700 dark:text-amber-400'}">
      <Icon name={healthy ? 'check' : 'warning'} class="h-4 w-4" />
      {healthy ? i18n.m.dashboard.health_ok : i18n.m.dashboard.needs_attention}
    </div>
    <div class="mt-1 text-xs text-slate-500 dark:text-slate-400">{healthDetail}</div>
  </div>
</div>
