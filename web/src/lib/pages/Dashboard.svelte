<script lang="ts">
  import { api, type Health, type ToolCheck, type Stats } from '../api'
  import { formatSize } from '../format'
  import { i18n, t, plural } from '../i18n/i18n.svelte'
  import { router } from '../stores/ui.svelte'
  import { activity } from '../stores/activity.svelte'
  import Banner from '../components/Banner.svelte'
  import Icon from '../components/Icon.svelte'
  import UsageGraph from '../components/UsageGraph.svelte'

  let health = $state<Health | null>(null)
  let tools = $state<ToolCheck[]>([])
  let stats = $state<Stats | null>(null)
  let error = $state<string | null>(null)

  // Reset flow for the lifetime savings tally: a two-step inline confirm so the headline
  // figure is never wiped on a stray click.
  let confirmingReset = $state(false)
  let resetting = $state(false)

  $effect(() => {
    void load()
    // Queue/quarantine figures change as work runs; refresh periodically.
    const timer = setInterval(load, 15000)
    return () => clearInterval(timer)
  })

  async function load() {
    error = null
    try {
      ;[health, tools, stats] = await Promise.all([api.health(), api.tools(), api.stats()])
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.dashboard.error_load
    }
  }

  async function resetSavings() {
    resetting = true
    error = null
    try {
      stats = await api.clearStats()
      confirmingReset = false
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.dashboard.error_reset
    } finally {
      resetting = false
    }
  }

  let toolsReady = $derived(tools.length > 0 && tools.every((t) => t.available))
  let healthy = $derived(health?.status === 'healthy' && toolsReady)
  let activeWork = $derived((stats?.running ?? 0) + (stats?.queued ?? 0))

  // Mirror the Queue view: GPU stats are only meaningful when the host can expose them
  // without elevation; otherwise the broadcaster reports the GPU unsupported.
  let gpuUnavailable = $derived(
    activity.metrics && !activity.metrics.gpuSupported ? i18n.m.dashboard.gpu_unavailable : null,
  )
</script>

<header class="mb-6">
  <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">{i18n.m.nav.dashboard}</h1>
  <p class="text-sm text-slate-500 dark:text-slate-400">{i18n.m.dashboard.subtitle}</p>
</header>

{#if error}
  <Banner kind="error" class="mb-6">{error}</Banner>
{/if}

<!-- Headline outcome: persistent lifetime space saved. This is a durable running total that
     survives restarts, quarantine purges, and history clearing — reset only on request. -->
<div class="card mb-4 p-6">
  <div class="flex items-start justify-between gap-3">
    <div class="label">{i18n.m.dashboard.total_saved} <span class="ml-1 font-normal normal-case tracking-normal text-slate-400">· {i18n.m.dashboard.lifetime}</span></div>
    {#if stats && stats.filesOptimised > 0}
      {#if confirmingReset}
        <div class="flex items-center gap-2 text-sm">
          <span class="text-slate-500 dark:text-slate-400">{i18n.m.dashboard.reset_confirm}</span>
          <button class="btn btn-danger px-2 py-1 text-xs" onclick={resetSavings} disabled={resetting}>{resetting ? i18n.m.dashboard.resetting : i18n.m.dashboard.reset}</button>
          <button class="btn px-2 py-1 text-xs" onclick={() => (confirmingReset = false)} disabled={resetting}>{i18n.m.common.cancel}</button>
        </div>
      {:else}
        <button
          class="flex items-center gap-1 text-xs text-slate-400 transition hover:text-slate-600 dark:hover:text-slate-200"
          title={i18n.m.dashboard.reset_title}
          onclick={() => (confirmingReset = true)}
        >
          <Icon name="trash" class="h-3.5 w-3.5" /> {i18n.m.dashboard.reset}
        </button>
      {/if}
    {/if}
  </div>
  {#if stats}
    <div class="mt-1 text-4xl font-bold tabular-nums text-emerald-600 dark:text-emerald-400">{formatSize(stats.bytesSaved)}</div>
    {#if stats.filesOptimised > 0}
      <div class="mt-1 text-sm text-slate-500 dark:text-slate-400">
        {t(stats.filesOptimised === 1 ? i18n.m.dashboard.files_optimised_one : i18n.m.dashboard.files_optimised_other, {
          count: stats.filesOptimised.toLocaleString(),
          from: formatSize(stats.originalBytes),
          to: formatSize(stats.optimisedBytes),
          percent: Math.round(stats.averageSavingPercent),
        })}
      </div>
    {:else}
      <div class="mt-1 text-sm text-slate-500 dark:text-slate-400">
        {i18n.m.dashboard.empty}
        <button class="text-cyan-600 hover:underline dark:text-cyan-400" onclick={() => router.go('/libraries')}>{i18n.m.dashboard.add_library}</button>
      </div>
    {/if}
  {:else}
    <div class="mt-1 text-4xl font-bold text-slate-300 dark:text-slate-700">—</div>
  {/if}
</div>

<div class="mb-4 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
  <!-- In flight -->
  <button class="card p-4 text-left transition hover:border-cyan-300 dark:hover:border-cyan-800" onclick={() => router.go('/queue')}>
    <div class="label">{i18n.m.nav.queue}</div>
    <div class="text-2xl font-semibold tabular-nums text-slate-800 dark:text-slate-100">{(stats?.running ?? 0).toLocaleString()} <span class="text-base font-normal text-slate-400">{i18n.m.dashboard.running}</span></div>
    <div class="text-sm text-slate-400">
      {t(i18n.m.dashboard.queued, { count: (stats?.queued ?? 0).toLocaleString() })}{#if (stats?.failed ?? 0) > 0} · <span class="text-red-600 dark:text-red-400">{t(i18n.m.dashboard.failed, { count: (stats?.failed ?? 0).toLocaleString() })}</span>{/if}
    </div>
  </button>

  <!-- Awaiting review (quarantine) -->
  <button class="card p-4 text-left transition hover:border-cyan-300 dark:hover:border-cyan-800" onclick={() => router.go('/quarantine')}>
    <div class="label">{i18n.m.dashboard.awaiting_review}</div>
    <div class="text-2xl font-semibold tabular-nums text-slate-800 dark:text-slate-100">{(stats?.inQuarantine ?? 0).toLocaleString()}</div>
    <div class="text-sm text-slate-400">
      {#if (stats?.inQuarantine ?? 0) > 0}{t(i18n.m.dashboard.reclaim_on_approve, { size: formatSize(stats?.quarantineReclaimableBytes ?? 0) })}{:else}{i18n.m.dashboard.in_quarantine}{/if}
    </div>
  </button>

  <!-- Ready to replace -->
  <button class="card p-4 text-left transition hover:border-cyan-300 dark:hover:border-cyan-800" onclick={() => router.go('/queue')}>
    <div class="label">{i18n.m.dashboard.ready_to_replace}</div>
    <div class="text-2xl font-semibold tabular-nums text-slate-800 dark:text-slate-100">{(stats?.readyToReplace ?? 0).toLocaleString()}</div>
    <div class="text-sm text-slate-400">{i18n.m.dashboard.verified_awaiting}</div>
  </button>

  <!-- Libraries -->
  <button class="card p-4 text-left transition hover:border-cyan-300 dark:hover:border-cyan-800" onclick={() => router.go('/libraries')}>
    <div class="label">{i18n.m.nav.libraries}</div>
    <div class="text-2xl font-semibold tabular-nums text-slate-800 dark:text-slate-100">{(stats?.libraries ?? 0).toLocaleString()}</div>
    <div class="text-sm text-slate-400">
      {t(i18n.m.dashboard.libraries_detail, { enabled: (stats?.enabledLibraries ?? 0).toLocaleString(), files: (stats?.discoveredFiles ?? 0).toLocaleString() })}
    </div>
  </button>
</div>

<!-- Live system usage: the same CPU/GPU telemetry the Queue view shows, pushed over SignalR while
     a job encodes. Reuses the app-wide activity store, so it is live regardless of which page opened it. -->
<div class="card mb-4 p-4">
  <div class="flex items-center justify-between gap-3">
    <div class="flex items-center gap-2">
      <Icon name="gpu" class="h-4 w-4 text-slate-400" />
      <div class="label">{i18n.m.dashboard.live_usage}</div>
    </div>
    {#if activity.activeJobs > 0}
      <span class="text-xs text-slate-400">{plural(activity.activeJobs, i18n.m.dashboard.jobs_running_one, i18n.m.dashboard.jobs_running_other, activity.activeJobs.toLocaleString())}</span>
    {/if}
  </div>
  {#if activity.metrics}
    <div class="mt-3 grid gap-3 sm:grid-cols-2">
      <UsageGraph label="CPU" data={activity.cpuHistory} current={activity.metrics.cpuPercent} color="rgb(56,189,248)" />
      <UsageGraph
        label="GPU"
        data={activity.gpuHistory}
        current={activity.metrics.gpuPercent}
        color="rgb(34,197,94)"
        unavailable={gpuUnavailable}
        detail={activity.metrics.gpuEngine}
      />
    </div>
  {:else}
    <p class="mt-2 text-sm text-slate-400">{i18n.m.dashboard.usage_hint}</p>
  {/if}
</div>

<!-- System health -->
<div class="card p-4">
  <div class="flex flex-wrap items-center justify-between gap-3">
    <div class="flex items-center gap-3">
      <span class="flex h-9 w-9 items-center justify-center rounded-full {healthy ? 'bg-emerald-100 text-emerald-600 dark:bg-emerald-900/40 dark:text-emerald-400' : 'bg-amber-100 text-amber-600 dark:bg-amber-950 dark:text-amber-300'}">
        <Icon name={healthy ? 'check' : 'warning'} class="h-5 w-5" />
      </span>
      <div>
        <div class="font-semibold text-slate-800 dark:text-slate-100">{healthy ? i18n.m.dashboard.healthy : i18n.m.dashboard.needs_attention}</div>
        <div class="text-sm text-slate-400">
          {t(i18n.m.dashboard.health_detail, {
            status: health?.status ?? i18n.m.dashboard.unknown_status,
            tools: toolsReady ? i18n.m.dashboard.tools_ready : i18n.m.dashboard.tools_not_detected,
          })}{#if activeWork > 0} · {plural(activeWork, i18n.m.dashboard.jobs_in_flight_one, i18n.m.dashboard.jobs_in_flight_other, activeWork.toLocaleString())}{/if}
        </div>
      </div>
    </div>
    <button class="btn" onclick={() => router.go('/settings')}>{i18n.m.dashboard.settings_tools}</button>
  </div>
</div>
