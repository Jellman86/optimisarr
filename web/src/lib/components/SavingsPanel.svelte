<script lang="ts">
  import type { Stats } from '../api'
  import { formatSize } from '../format'
  import { i18n, t } from '../i18n/i18n.svelte'
  import Icon from './Icon.svelte'
  import InfoTip from './InfoTip.svelte'
  import OkMark from './OkMark.svelte'
  import PanelHeader from './PanelHeader.svelte'

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

{#snippet resetControls()}
  {#if confirmingReset}
    <button class="btn btn-sm btn-danger" onclick={onreset} disabled={resetting}>{resetting ? i18n.m.dashboard.resetting : i18n.m.dashboard.reset}</button>
    <button class="btn btn-sm" onclick={() => (confirmingReset = false)} disabled={resetting}>{i18n.m.common.cancel}</button>
  {:else}
    <button
      class="btn btn-sm btn-ghost btn-icon text-ink-4"
      title={i18n.m.dashboard.reset_title}
      aria-label={i18n.m.dashboard.reset_title}
      onclick={() => (confirmingReset = true)}
    ><Icon name="trash" class="h-3.5 w-3.5" /></button>
  {/if}
{/snippet}

<section class="card flex h-full flex-col" aria-labelledby="savings-heading">
  <PanelHeader as="h2" id="savings-heading" title={i18n.m.dashboard.total_saved} trailing={optimised ? resetControls : undefined} />

  <div class="flex flex-1 flex-col gap-4 p-4">
    <div>
      <div class="text-display font-bold tracking-[-0.03em] tabular-nums text-ink">
        {stats ? formatSize(stats.bytesSaved) : '—'}
      </div>
      <p class="mt-2 text-sm text-ink-2">
        {optimised && stats
          ? t(stats.filesOptimised === 1 ? i18n.m.dashboard.saved_detail_one : i18n.m.dashboard.saved_detail_other, { count: stats.filesOptimised.toLocaleString(), percent: Math.round(stats.averageSavingPercent) })
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
        <p class="mt-2 text-xs tabular-nums text-ink-3">
          {t(i18n.m.dashboard.originals_now, { original: formatSize(stats.originalBytes), optimised: formatSize(stats.optimisedBytes) })}
        </p>
      </div>
    {/if}
  </div>

  <!-- Health earns attention only when it changes: a quiet line that turns amber and says what is
       missing, rather than a card of its own for a status that is true almost always. -->
  <dl class="hairline-t m-0 grid grid-cols-2 text-xs">
    <div class="px-4 py-3">
      <dt class="label mb-0.5">{i18n.m.nav.libraries}</dt>
      <dd class="m-0 text-ink-2">
        {t(i18n.m.dashboard.libraries_detail, {
          enabled: (stats?.enabledLibraries ?? 0).toLocaleString(),
          files: (stats?.discoveredFiles ?? 0).toLocaleString(),
        })}
      </dd>
    </div>
    <div class="border-l border-line-soft px-4 py-3">
      <dt class="label mb-0.5">{i18n.m.dashboard.health}</dt>
      <dd class="m-0 flex items-center gap-1.5 {healthy ? 'text-ink-2' : 'text-warn'}">
        {#if healthy}<OkMark />{:else}<Icon name="warning" class="h-3.5 w-3.5" />{/if}
        {healthy ? i18n.m.dashboard.health_ok : i18n.m.dashboard.needs_attention}
        <InfoTip label={t(i18n.m.common.about_information, { label: i18n.m.dashboard.health })} text={healthDetail} />
      </dd>
    </div>
  </dl>
</section>
