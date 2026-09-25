<script lang="ts">
  import type { Stats } from '../api'
  import { formatSize } from '../format'
  import { i18n, t } from '../i18n/i18n.svelte'
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

  // Every figure in this rail is a field the server sent. Nothing here is derived by subtracting
  // one of its fields from another: filesOptimised is a lifetime tally and discoveredFiles is the
  // inventory as it stands, so their difference is not "work left to do" — checked against a live
  // server it read 5,129 against a queue of 14, because most of the difference is simply not
  // eligible. A dashboard that overstates what it can win is what this application exists not to be.
</script>

<div class="telemetry-container">
<div class="telemetry-grid card grid">
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
            class="text-ink-4 transition hover:text-ink-2"
            title={i18n.m.dashboard.reset_title}
            aria-label={i18n.m.dashboard.reset_title}
            onclick={() => (confirmingReset = true)}
          ><Icon name="trash" class="h-3.5 w-3.5" /></button>
        {/if}
      {/if}
    </div>
    <div class="mt-1.5 font-mono text-xl font-semibold tabular-nums text-ok">
      {stats ? formatSize(stats.bytesSaved) : '—'}
    </div>
    <div class="mt-1 text-xs text-ink-3">
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
    <div class="label mb-0">{i18n.m.dashboard.in_the_queue}</div>
    <div class="mt-1.5 font-mono text-xl font-semibold tabular-nums text-ink">
      {(stats?.queued ?? 0).toLocaleString()}
    </div>
    <div class="mt-1 text-xs text-ink-3">
      {t(i18n.m.dashboard.queue_detail, {
        running: (stats?.running ?? 0).toLocaleString(),
        failed: (stats?.failed ?? 0).toLocaleString(),
      })}
    </div>
  </div>

  <div class="p-4">
    <div class="label mb-0">{i18n.m.nav.libraries}</div>
    <div class="mt-1.5 font-mono text-xl font-semibold tabular-nums text-ink">
      {(stats?.libraries ?? 0).toLocaleString()}
    </div>
    <div class="mt-1 text-xs text-ink-3">
      {t(i18n.m.dashboard.libraries_detail, {
        enabled: (stats?.enabledLibraries ?? 0).toLocaleString(),
        files: (stats?.discoveredFiles ?? 0).toLocaleString(),
      })}
    </div>
  </div>

  <!-- Health earns attention only when it changes. A status that is true almost always does not
       deserve a card of its own; it deserves a cell that goes amber and says what is missing. -->
  <div class="p-4">
    <div class="label mb-0">{i18n.m.dashboard.health}</div>
    <div class="mt-1.5 flex items-center gap-2 font-mono text-xl font-semibold {healthy ? 'text-ok' : 'text-warn'}">
      <Icon name={healthy ? 'check' : 'warning'} class="h-4 w-4" />
      {healthy ? i18n.m.dashboard.health_ok : i18n.m.dashboard.needs_attention}
    </div>
    <div class="mt-1 text-xs text-ink-3">{healthDetail}</div>
  </div>
</div>
</div>

<style>
  .telemetry-container { container-type: inline-size; }
  .telemetry-grid { grid-template-columns: minmax(0, 1fr); }
  .telemetry-grid > div + div { border-top: 1px solid var(--divide); }
  @container (min-width: 30rem) {
    .telemetry-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
    .telemetry-grid > div:nth-child(2) { border-top: 0; }
    .telemetry-grid > div:nth-child(even) { border-left: 1px solid var(--divide); }
  }
  @container (min-width: 60rem) {
    .telemetry-grid { grid-template-columns: repeat(4, minmax(0, 1fr)); }
    .telemetry-grid > div { border-top: 0; }
    .telemetry-grid > div:nth-child(3) { border-left: 1px solid var(--divide); }
  }
</style>
