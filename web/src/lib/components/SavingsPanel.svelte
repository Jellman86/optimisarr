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

  // Every figure here is a field the server sent. Nothing is derived by subtracting one field from
  // another: filesOptimised is a lifetime tally and discoveredFiles is the inventory as it stands,
  // so their difference is not "work left to do" — most of it is simply not eligible.
  let optimised = $derived(stats != null && stats.filesOptimised > 0)
  let keptShare = $derived(stats && stats.originalBytes > 0 ? Math.min(100, (stats.optimisedBytes / stats.originalBytes) * 100) : 0)
</script>

<section class="card flex h-full flex-col" aria-labelledby="savings-heading">
  <div class="flex items-center gap-3 border-b border-line px-4 py-2.5">
    <h2 id="savings-heading" class="label mb-0">{i18n.m.dashboard.total_saved}</h2>
    {#if optimised}
      {#if confirmingReset}
        <span class="ml-auto flex items-center gap-1">
          <button class="btn btn-danger px-2 py-0.5 text-[10px]" onclick={onreset} disabled={resetting}>{resetting ? i18n.m.dashboard.resetting : i18n.m.dashboard.reset}</button>
          <button class="btn px-2 py-0.5 text-[10px]" onclick={() => (confirmingReset = false)} disabled={resetting}>{i18n.m.common.cancel}</button>
        </span>
      {:else}
        <button
          class="ml-auto rounded text-ink-4 transition hover:text-ink-2 focus-ring"
          title={i18n.m.dashboard.reset_title}
          aria-label={i18n.m.dashboard.reset_title}
          onclick={() => (confirmingReset = true)}
        ><Icon name="trash" class="h-3.5 w-3.5" /></button>
      {/if}
    {/if}
  </div>

  <div class="flex flex-1 flex-col gap-4 p-4">
    <div>
      <div class="font-mono text-4xl font-semibold leading-none tracking-tight tabular-nums text-ok">
        {stats ? formatSize(stats.bytesSaved) : '—'}
      </div>
      <p class="mt-2 text-sm text-ink-2">
        {optimised && stats
          ? t(i18n.m.dashboard.saved_detail, { count: stats.filesOptimised.toLocaleString(), percent: Math.round(stats.averageSavingPercent) })
          : i18n.m.dashboard.empty_short}
      </p>
    </div>

    {#if optimised && stats}
      <!-- What the originals took against what the copies take now: the saving as a share. -->
      <div class="mt-auto">
        <div class="flex h-2.5 overflow-hidden rounded-full bg-[var(--raised)]" aria-hidden="true">
          <div class="h-full bg-ink-4" style="width: {keptShare}%"></div>
          <div class="h-full flex-1 bg-ok/80"></div>
        </div>
        <p class="mt-2 font-mono text-xs text-ink-3">
          {t(i18n.m.dashboard.originals_now, { original: formatSize(stats.originalBytes), optimised: formatSize(stats.optimisedBytes) })}
        </p>
      </div>
    {/if}
  </div>

  <!-- Health earns attention only when it changes: a quiet line that turns amber and says what is
       missing, rather than a card of its own for a status that is true almost always. -->
  <dl class="m-0 grid grid-cols-2 border-t border-line text-xs">
    <div class="px-4 py-3">
      <dt class="label mb-0.5">{i18n.m.nav.libraries}</dt>
      <dd class="m-0 text-ink-2">
        {t(i18n.m.dashboard.libraries_detail, {
          enabled: (stats?.enabledLibraries ?? 0).toLocaleString(),
          files: (stats?.discoveredFiles ?? 0).toLocaleString(),
        })}
      </dd>
    </div>
    <div class="border-l border-line px-4 py-3">
      <dt class="label mb-0.5">{i18n.m.dashboard.health}</dt>
      <dd class="m-0 flex items-center gap-1.5 {healthy ? 'text-ok' : 'text-warn'}" title={healthDetail}>
        <Icon name={healthy ? 'check' : 'warning'} class="h-3.5 w-3.5" />
        {healthy ? i18n.m.dashboard.health_ok : i18n.m.dashboard.needs_attention}
      </dd>
    </div>
  </dl>
</section>
