<script lang="ts">
  import type { FailureGroup, Stats } from '../api'
  import { formatSize } from '../format'
  import { i18n, t } from '../i18n/i18n.svelte'
  import { router } from '../stores/ui.svelte'
  import Icon from './Icon.svelte'

  let { stats, failures = [] }: { stats: Stats | null; failures?: FailureGroup[] } = $props()

  // "11 failed" says nothing a person can act on. The categories say where to look, so the
  // summary line names the causes in order of how many jobs each one accounts for.
  let failureSummary = $derived(
    [...failures]
      .sort((a, b) => b.count - a.count)
      .slice(0, 3)
      .map((group) => `${group.description || group.category} ×${group.count}`)
      .join(' · '),
  )

  let ready = $derived(stats?.readyToReplace ?? 0)
  let quarantined = $derived(stats?.inQuarantine ?? 0)
  let failed = $derived(stats?.failed ?? 0)
  let nothingWaiting = $derived(ready === 0 && quarantined === 0 && failed === 0)
</script>

<div class="card">
  <div class="flex flex-wrap items-center gap-3 border-b border-slate-200 px-4 py-2.5 dark:border-slate-700">
    <span class="label mb-0">{i18n.m.dashboard.needs_you}</span>
  </div>

  {#if nothingWaiting}
    <p class="px-4 py-5 text-sm text-slate-500 dark:text-slate-400">{i18n.m.dashboard.needs_you_none}</p>
  {:else}
    <ul class="m-0 list-none p-0">
      {#if ready > 0}
        <li class="flex items-center gap-3 border-b border-slate-200 px-4 py-3 last:border-b-0 dark:border-slate-700">
          <span class="flex w-16 flex-none items-center gap-2 text-emerald-700 dark:text-emerald-400">
            <Icon name="check" class="h-4 w-4" />
            <span class="font-mono text-lg font-medium tabular-nums">{ready.toLocaleString()}</span>
          </span>
          <div class="min-w-0 flex-1">
            <div class="text-sm font-medium text-slate-800 dark:text-slate-100">{i18n.m.dashboard.ready_to_replace}</div>
            <div class="mt-0.5 text-xs text-slate-500 dark:text-slate-400">{i18n.m.dashboard.ready_detail}</div>
          </div>
          <button class="btn btn-primary flex-none px-3 py-1.5 text-xs" onclick={() => router.go('/queue')}>{i18n.m.dashboard.action_replace}</button>
        </li>
      {/if}

      {#if quarantined > 0}
        <li class="flex items-center gap-3 border-b border-slate-200 px-4 py-3 last:border-b-0 dark:border-slate-700">
          <span class="flex w-16 flex-none items-center gap-2 text-slate-600 dark:text-slate-300">
            <Icon name="clock" class="h-4 w-4" />
            <span class="font-mono text-lg font-medium tabular-nums">{quarantined.toLocaleString()}</span>
          </span>
          <div class="min-w-0 flex-1">
            <div class="text-sm font-medium text-slate-800 dark:text-slate-100">{i18n.m.dashboard.awaiting_review}</div>
            <div class="mt-0.5 text-xs text-slate-500 dark:text-slate-400">
              {t(i18n.m.dashboard.reclaim_on_approve, { size: formatSize(stats?.quarantineReclaimableBytes ?? 0) })}
            </div>
          </div>
          <button class="btn flex-none px-3 py-1.5 text-xs" onclick={() => router.go('/quarantine')}>{i18n.m.dashboard.action_review}</button>
        </li>
      {/if}

      {#if failed > 0}
        <li class="flex items-center gap-3 border-b border-slate-200 px-4 py-3 last:border-b-0 dark:border-slate-700">
          <span class="flex w-16 flex-none items-center gap-2 text-red-600 dark:text-red-400">
            <Icon name="warning" class="h-4 w-4" />
            <span class="font-mono text-lg font-medium tabular-nums">{failed.toLocaleString()}</span>
          </span>
          <div class="min-w-0 flex-1">
            <div class="text-sm font-medium text-slate-800 dark:text-slate-100">{i18n.m.dashboard.failed_heading}</div>
            <div class="mt-0.5 text-xs text-slate-500 dark:text-slate-400">
              {failureSummary || i18n.m.dashboard.failed_nothing_replaced}
            </div>
          </div>
          <button class="btn flex-none px-3 py-1.5 text-xs" onclick={() => router.go('/queue')}>{i18n.m.dashboard.action_inspect}</button>
        </li>
      {/if}
    </ul>
  {/if}
</div>
