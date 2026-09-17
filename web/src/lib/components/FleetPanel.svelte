<script lang="ts">
  import type { Worker } from '../api'
  import { formatSize } from '../format'
  import { i18n, plural, t } from '../i18n/i18n.svelte'
  import { activity } from '../stores/activity.svelte'
  import { router } from '../stores/ui.svelte'

  let {
    workers = [],
    workersAvailable = false,
    runningLocally = 0,
  }: { workers?: Worker[]; workersAvailable?: boolean; runningLocally?: number } = $props()

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

<div class="card">
  <div class="flex flex-wrap items-center gap-3 border-b border-line px-4 py-2.5">
    <span class="label mb-0">{i18n.m.dashboard.fleet}</span>
    {#if workersAvailable}
      <span class="ml-auto font-mono text-xs text-ink-3">
        {t(i18n.m.dashboard.fleet_reporting, { online: online.toLocaleString(), total: total.toLocaleString() })}
      </span>
    {/if}
  </div>

  <ul class="m-0 list-none p-0">
    <!-- This server is a row in the same list, with the same fields. Once a sidecar can take work,
         the container is one machine among several rather than the subject of the page. -->
    <li class="grid gap-3 border-b border-line px-4 py-3 last:border-b-0 sm:grid-cols-[1fr_150px_100px] sm:items-center">
      <div class="min-w-0">
        <div class="flex items-center gap-2 text-sm font-semibold text-ink">
          <span class="h-1.5 w-1.5 flex-none rounded-full bg-ok" aria-hidden="true"></span>
          {i18n.m.dashboard.this_server}
        </div>
        <div class="mt-1 font-mono text-xs text-ink-3">
          {plural(runningLocally, i18n.m.dashboard.fleet_jobs_one, i18n.m.dashboard.fleet_jobs_other, runningLocally.toLocaleString())}
        </div>
      </div>
      <div class="font-mono text-[10px] text-ink-2">
        <div class="mb-1 flex items-center gap-2">
          <span class="w-7">CPU</span>
          <span class="h-[3px] flex-1 overflow-hidden rounded-full bg-sunken"><span class="block h-full rounded-full bg-ink-3" style="width: {localCpu ?? 0}%"></span></span>
          <span class="w-8 text-right tabular-nums">{localCpu != null ? `${localCpu}%` : '—'}</span>
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
      <div class="font-mono text-xs tabular-nums text-ink-3 sm:text-right"></div>
    </li>

    {#each active as worker (worker.id)}
      {@const cpu = fraction(worker.cpuBusyFraction)}
      {@const gpu = fraction(worker.gpuBusyFraction)}
      <li class="grid gap-3 border-b border-line px-4 py-3 last:border-b-0 sm:grid-cols-[1fr_150px_100px] sm:items-center">
        <div class="min-w-0">
          <div class="flex items-center gap-2 text-sm font-semibold text-ink">
            <span
              class="h-1.5 w-1.5 flex-none rounded-full {worker.online ? 'bg-ok' : 'bg-ink-4'}"
              aria-hidden="true"
            ></span>
            <span class="truncate">{worker.name}</span>
          </div>
          <div class="mt-1 truncate font-mono text-xs text-ink-3">
            {worker.online ? workerSubtitle(worker) : i18n.m.dashboard.fleet_offline}
          </div>
          {#if worker.lastProblem}
            <!-- The server's most recent objection to this worker. Without it, a machine quietly
                 refusing every job is invisible until someone goes looking for it. -->
            <div class="mt-1 text-xs text-warn">{worker.lastProblem}</div>
          {/if}
        </div>
        <div class="font-mono text-[10px] text-ink-2">
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
        <div class="font-mono text-xs tabular-nums text-ink-3 sm:text-right">
          {worker.online ? formatSize(worker.freeScratchBytes) : '—'}
        </div>
      </li>
    {/each}
  </ul>

  {#if workersAvailable && active.length === 0}
    <div class="border-t border-line px-4 py-3 text-sm text-ink-3">
      {i18n.m.dashboard.fleet_no_workers}
      <button class="text-accent hover:underline" onclick={() => router.go('/workers')}>{i18n.m.dashboard.fleet_pair}</button>
    </div>
  {/if}
</div>
