<script lang="ts">
  import type { FailureGroup, Stats } from '../api'
  import { formatSize } from '../format'
  import { i18n, t } from '../i18n/i18n.svelte'
  import { router } from '../stores/ui.svelte'
  import Icon from './Icon.svelte'
  import OkMark from './OkMark.svelte'
  import PanelHeader from './PanelHeader.svelte'
  import { jobFailureShortLabel } from '../i18n/jobErrors'

  let { stats, failures = [] }: { stats: Stats | null; failures?: FailureGroup[] } = $props()

  // "144 failed" says nothing a person can act on. The categories say where to look, so the
  // summary names the causes in order of how many jobs each accounts for.
  let failureSummary = $derived(
    [...failures]
      .sort((a, b) => b.count - a.count)
      .slice(0, 3)
      .map((group) => `${group.count.toLocaleString()} ${jobFailureShortLabel(group.category, i18n.m, group.description)}`)
      .join(' · '),
  )

  let ready = $derived(stats?.readyToReplace ?? 0)
  let quarantined = $derived(stats?.inQuarantine ?? 0)
  let failed = $derived(stats?.failed ?? 0)
  let itemCount = $derived((ready > 0 ? 1 : 0) + (quarantined > 0 ? 1 : 0) + (failed > 0 ? 1 : 0))
  let nothingWaiting = $derived(itemCount === 0)
  let wideGrid = $derived(
    itemCount === 2 ? '@3xl:grid-cols-2 @3xl:divide-x @3xl:divide-y-0'
    : itemCount >= 3 ? '@3xl:grid-cols-3 @3xl:divide-x @3xl:divide-y-0'
    : '',
  )
  let wideItem = $derived(itemCount >= 2 ? '@3xl:flex-col @3xl:items-start @3xl:gap-2' : '')
  let wideCount = $derived(itemCount >= 2 ? '@3xl:min-w-0' : '')
</script>

<!-- Wide enough to lead the page across its full width, multiple decisions sit side by side in
     balanced columns instead of stretching across the display; a single decision stays one tidy
     row across the full width, and narrow views stack. -->
<section class="@container card h-full" aria-labelledby="needs-you-heading">
  <PanelHeader as="h2" id="needs-you-heading" title={i18n.m.dashboard.needs_you} />

  {#if nothingWaiting}
    <p class="px-4 py-5 text-sm text-ink-3">{i18n.m.dashboard.needs_you_none}</p>
  {:else}
    <ul class="m-0 grid list-none divide-y divide-line-soft p-0 {wideGrid}">
      {#if ready > 0}
        <li class="flex items-center gap-3 px-4 py-3 {wideItem}">
          <span class="flex min-w-16 flex-none items-center gap-2 text-ink {wideCount}">
            <OkMark class="h-4 w-4" />
            <span class="text-title font-semibold tabular-nums">{ready.toLocaleString()}</span>
          </span>
          <div class="min-w-0 flex-1">
            <div class="text-sm font-medium text-ink">{i18n.m.dashboard.ready_to_replace}</div>
            <div class="mt-0.5 text-xs text-ink-3">{i18n.m.dashboard.ready_detail}</div>
          </div>
          <button type="button" class="btn btn-sm btn-primary flex-none" onclick={() => router.go('/queue')}>{i18n.m.dashboard.action_replace}</button>
        </li>
      {/if}

      {#if quarantined > 0}
        <li class="flex items-center gap-3 px-4 py-3 {wideItem}">
          <span class="flex min-w-16 flex-none items-center gap-2 text-ink-2 {wideCount}">
            <Icon name="clock" class="h-4 w-4" />
            <span class="text-title font-semibold tabular-nums">{quarantined.toLocaleString()}</span>
          </span>
          <div class="min-w-0 flex-1">
            <div class="text-sm font-medium text-ink">{i18n.m.dashboard.awaiting_review}</div>
            <div class="mt-0.5 text-xs text-ink-3">
              {t(i18n.m.dashboard.reclaim_on_approve, { size: formatSize(stats?.quarantineReclaimableBytes ?? 0) })}
            </div>
          </div>
          <button type="button" class="btn btn-sm flex-none" onclick={() => router.go('/quarantine')}>{i18n.m.dashboard.action_review}</button>
        </li>
      {/if}

      {#if failed > 0}
        <li class="flex items-center gap-3 px-4 py-3 {wideItem}">
          <span class="flex min-w-16 flex-none items-center gap-2 text-bad {wideCount}">
            <Icon name="warning" class="h-4 w-4" />
            <span class="text-title font-semibold tabular-nums">{failed.toLocaleString()}</span>
          </span>
          <div class="min-w-0 flex-1">
            <div class="text-sm font-medium text-ink">{i18n.m.dashboard.failed_heading}</div>
            <div class="mt-0.5 text-xs text-ink-3">{failureSummary || i18n.m.dashboard.failed_nothing_replaced}</div>
          </div>
          <button type="button" class="btn btn-sm flex-none" onclick={() => router.go('/queue/failures')}>{i18n.m.dashboard.action_inspect}</button>
        </li>
      {/if}
    </ul>
  {/if}
</section>
