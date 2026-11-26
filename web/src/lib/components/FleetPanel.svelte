<script lang="ts">
  import type { Worker } from '../api'
  import { formatRelative, formatSize } from '../format'
  import { i18n, plural, t } from '../i18n/i18n.svelte'
  import { activity } from '../stores/activity.svelte'
  import { router } from '../stores/ui.svelte'
  import PanelHeader from './PanelHeader.svelte'

  let {
    workers = [],
    workersAvailable = false,
    runningLocally = 0,
    localFreeBytes = null,
  }: {
    workers?: Worker[]
    workersAvailable?: boolean
    runningLocally?: number
    /** Free space on this server's work disk, when the queue status knows it. */
    localFreeBytes?: number | null
  } = $props()

  // A machine that cannot measure itself reports nothing, and is drawn as reporting nothing.
  // "Idle" and "no answer" are different answers to give someone deciding where work should go —
  // and a hardware encode can run on a media engine that reports no utilisation at all, so a low
  // number here never means an unused machine either.
  function fraction(value: number | null): number | null {
    return value == null ? null : Math.max(0, Math.min(100, Math.round(value * 100)))
  }

  let localCpu = $derived(activity.metrics ? Math.round(activity.metrics.cpuPercent) : null)
  let localGpu = $derived(
    activity.metrics && activity.metrics.gpuSupported && activity.metrics.gpuPercent != null
      ? Math.round(activity.metrics.gpuPercent)
      : null,
  )

  // A problem is news for a day; after that it is history, still worth reading but not a warning.
  const PROBLEM_FRESH_MS = 24 * 60 * 60 * 1000
  function problemIsFresh(worker: Worker): boolean {
    return worker.lastProblemAt != null && Date.now() - new Date(worker.lastProblemAt).getTime() < PROBLEM_FRESH_MS
  }

  function problemLine(worker: Worker): string {
    const when = worker.lastProblemAt ? formatRelative(worker.lastProblemAt, new Date(), i18n.locale) : null
    return when ? `${when} · ${worker.lastProblem}` : worker.lastProblem ?? ''
  }

  // Built here rather than in the template: Svelte trims the leading space out of an inline
  // {#if} block, which silently rendered "1 job· 0.1.4".
  function workerSubtitle(worker: Worker): string {
    const jobs = plural(
      worker.heldLeases,
      i18n.m.dashboard.fleet_jobs_one,
      i18n.m.dashboard.fleet_jobs_other,
      worker.heldLeases.toLocaleString(),
    )
    return worker.sidecarVersion ? `${jobs} · ${worker.sidecarVersion}` : jobs
  }

  let active = $derived(workers.filter((worker) => !worker.revokedAt))
  // This server is a machine in this list, so it counts in the tally above it. Counting only the
  // sidecars made a three-row panel say "1 of 2".
  let online = $derived(active.filter((worker) => worker.online).length + 1)
  let total = $derived(active.length + 1)
</script>

<!-- A size on its own line on a phone, or under a column header on a wider screen, is labelled
     either way: the column header is drawing, and each cell carries its own name for a reader. -->
{#snippet freeSpace(bytes: number | null)}
  <div class="flex items-baseline gap-1.5 text-xs tabular-nums text-ink-3 @md:block @md:text-right">
    <span class="text-micro @md:sr-only">{i18n.m.dashboard.fleet_col_free_space}</span>
    {#if bytes != null}
      <span class="text-ink-2">{formatSize(bytes)}</span>
    {:else}
      <span aria-hidden="true">—</span><span class="sr-only">{i18n.m.common.unknown}</span>
    {/if}
  </div>
{/snippet}

{#snippet reporting()}
  <span class="text-xs text-ink-3">
    {t(i18n.m.dashboard.fleet_reporting, { online: online.toLocaleString(), total: total.toLocaleString() })}
  </span>
{/snippet}

<section class="@container card h-full" aria-labelledby="fleet-heading">
  <PanelHeader as="h2" id="fleet-heading" title={i18n.m.dashboard.fleet} trailing={workersAvailable ? reporting : undefined} />

  <div class="hairline-b hidden grid-cols-[minmax(0,1fr)_150px_100px] gap-3 px-4 py-2 text-micro font-semibold text-ink-3 @md:grid" aria-hidden="true" data-fleet-columns>
    <span>{i18n.m.dashboard.fleet_col_machine}</span>
    <span>{i18n.m.dashboard.fleet_col_load}</span>
    <span class="text-right">{i18n.m.dashboard.fleet_col_free_space}</span>
  </div>

  <ul class="m-0 list-none divide-y divide-line-soft p-0">
    <!-- This server is a row in the same list, with the same fields. Once a sidecar can take work,
         the container is one machine among several rather than the subject of the page. -->
    <li class="grid gap-3 px-4 py-3 @md:grid-cols-[minmax(0,1fr)_150px_100px] @md:items-center">
      <div class="min-w-0">
        <div class="flex items-center gap-2 text-sm font-semibold text-ink">
          <span class="h-1.5 w-1.5 flex-none rounded-full bg-ok" aria-hidden="true"></span>
          {i18n.m.dashboard.this_server}
        </div>
        <div class="mt-1 text-xs text-ink-3">
          {plural(runningLocally, i18n.m.dashboard.fleet_jobs_one, i18n.m.dashboard.fleet_jobs_other, runningLocally.toLocaleString())}
        </div>
      </div>
      <div class="text-micro font-medium text-ink-3">
        <div class="mb-1 flex items-center gap-2">
          <span class="w-7">CPU</span>
          <span class="h-[3px] flex-1 overflow-hidden rounded-full bg-sunken"><span class="block h-full rounded-full bg-ink-3" style="width: {localCpu ?? 0}%"></span></span>
          <span class="w-8 text-right tabular-nums">{#if localCpu != null}{localCpu}%{:else}<span aria-hidden="true">—</span><span class="sr-only">{i18n.m.common.unknown}</span>{/if}</span>
        </div>
        {#if localGpu != null}
          <div class="flex items-center gap-2">
            <span class="w-7">GPU</span>
            <span class="h-[3px] flex-1 overflow-hidden rounded-full bg-sunken"><span class="block h-full rounded-full bg-ink-3" style="width: {localGpu}%"></span></span>
            <span class="w-8 text-right tabular-nums">{localGpu}%</span>
          </div>
        {:else}
          <div class="text-ink-3">{i18n.m.dashboard.gpu_not_reported}</div>
        {/if}
      </div>
      {@render freeSpace(localFreeBytes)}
    </li>

    {#each active as worker (worker.id)}
      {@const cpu = fraction(worker.cpuBusyFraction)}
      {@const gpu = fraction(worker.gpuBusyFraction)}
      <li class="grid gap-3 px-4 py-3 @md:grid-cols-[minmax(0,1fr)_150px_100px] @md:items-center">
        <div class="min-w-0">
          <div class="flex items-center gap-2 text-sm font-semibold text-ink">
            <span
              class="h-1.5 w-1.5 flex-none rounded-full {worker.online ? 'bg-ok' : 'bg-ink-4'}"
              aria-hidden="true"
            ></span>
            <span class="truncate">{worker.name}</span>
          </div>
          <div class="mt-1 truncate text-xs text-ink-3">
            {worker.online ? workerSubtitle(worker) : i18n.m.dashboard.fleet_offline}
          </div>
          {#if worker.lastProblem}
            <!-- The server's most recent objection to this worker. Without it, a machine quietly
                 refusing every job is invisible until someone goes looking for it. -->
            <div class="mt-1 line-clamp-2 text-xs {problemIsFresh(worker) ? 'text-warn' : 'text-ink-4'}" title={worker.lastProblem}>{problemLine(worker)}</div>
          {/if}
        </div>
        <div class="text-micro font-medium text-ink-3">
          {#if cpu != null}
            <div class="mb-1 flex items-center gap-2">
              <span class="w-7">CPU</span>
              <span class="h-[3px] flex-1 overflow-hidden rounded-full bg-sunken"><span class="block h-full rounded-full bg-ink-3" style="width: {cpu}%"></span></span>
              <span class="w-8 text-right tabular-nums">{cpu}%</span>
            </div>
          {/if}
          {#if gpu != null}
            <div class="flex items-center gap-2">
              <span class="w-7">GPU</span>
              <span class="h-[3px] flex-1 overflow-hidden rounded-full bg-sunken"><span class="block h-full rounded-full bg-ink-3" style="width: {gpu}%"></span></span>
              <span class="w-8 text-right tabular-nums">{gpu}%</span>
            </div>
          {:else if cpu == null}
            <div class="text-ink-3">{i18n.m.dashboard.not_reporting}</div>
          {:else}
            <div class="text-ink-3">{i18n.m.dashboard.gpu_not_reported}</div>
          {/if}
        </div>
        {@render freeSpace(worker.online ? worker.freeScratchBytes : null)}
      </li>
    {/each}
  </ul>

  {#if workersAvailable && active.length === 0}
    <div class="hairline-t px-4 py-3 text-sm text-ink-3">
      {i18n.m.dashboard.fleet_no_workers}
      <button type="button" class="rounded-sm text-accent hover:underline focus-ring" onclick={() => router.go('/settings/workers')}>{i18n.m.dashboard.fleet_pair}</button>
    </div>
  {/if}
</section>
