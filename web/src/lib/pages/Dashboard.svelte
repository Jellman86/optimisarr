<script lang="ts">
  import {
    api,
    type FailureGroup,
    type Health,
    type Job,
    type QueueStatus,
    type Stats,
    type ToolCheck,
    type Worker,
  } from '../api'
  import { dashboardState } from '../dashboard-state'
  import { i18n, t } from '../i18n/i18n.svelte'
  import { activity } from '../stores/activity.svelte'
  import Banner from '../components/Banner.svelte'
  import DashboardStatusBar from '../components/DashboardStatusBar.svelte'
  import FleetPanel from '../components/FleetPanel.svelte'
  import InFlightPanel from '../components/InFlightPanel.svelte'
  import NeedsYouPanel from '../components/NeedsYouPanel.svelte'
  import TelemetryRail from '../components/TelemetryRail.svelte'

  let health = $state<Health | null>(null)
  let tools = $state<ToolCheck[]>([])
  let stats = $state<Stats | null>(null)
  let queue = $state<QueueStatus | null>(null)
  let jobs = $state<Job[]>([])
  let failures = $state<FailureGroup[]>([])
  let workers = $state<Worker[]>([])
  let workersAvailable = $state(false)
  let error = $state<string | null>(null)

  // Reset flow for the lifetime savings tally: a two-step inline confirm so the headline
  // figure is never wiped on a stray click.
  let confirmingReset = $state(false)
  let resetting = $state(false)

  $effect(() => {
    void load()
    // Queue, fleet and quarantine figures all change as work runs; refresh periodically.
    const timer = setInterval(load, 15000)
    return () => clearInterval(timer)
  })

  async function load() {
    error = null
    try {
      // The queue's own answer drives the whole page, so a failure here is a real error.
      // The rest degrade: a missing failure breakdown costs a subtitle, not the screen.
      const [healthResult, toolsResult, statsResult, queueResult, jobsResult] = await Promise.all([
        api.health(),
        api.tools(),
        api.stats(),
        api.queueStatus(),
        api.jobs(),
      ])
      health = healthResult
      tools = toolsResult
      stats = statsResult
      queue = queueResult
      jobs = jobsResult

      failures = await api.jobFailures().catch(() => [])
      await loadFleet()
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.dashboard.error_load
    }
  }

  // Remote workers are opt-in and only exist when the server was started with the experimental
  // switch on. Every worker route answers 403 otherwise, so the Fleet panel asks the settings
  // first and simply shows this server alone when the answer is no.
  async function loadFleet() {
    try {
      const settings = await api.settings()
      workersAvailable = settings.remoteWorkersAvailable && settings.remoteWorkersEnabled
      workers = workersAvailable ? await api.workers() : []
    } catch {
      workersAvailable = false
      workers = []
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

  let toolsReady = $derived(tools.length > 0 && tools.filter((tool) => tool.required).every((tool) => tool.available))
  let healthy = $derived(health?.status === 'healthy' && toolsReady)
  let healthDetail = $derived(
    t(i18n.m.dashboard.health_detail, {
      status: health?.status ?? i18n.m.dashboard.unknown_status,
      tools: toolsReady ? i18n.m.dashboard.tools_ready : i18n.m.dashboard.tools_not_detected,
    }),
  )

  // Jobs the server considers live. The queue's running count is authoritative for the status
  // bar; this list is what those jobs actually are.
  let runningJobs = $derived(
    jobs.filter((job) => job.startedAt !== null && job.finishedAt === null && job.status !== 'Failed'),
  )
  let runningLocally = $derived(runningJobs.filter((job) => !job.workerName).length)

  let queueState = $derived(
    queue
      ? dashboardState({
          canStart: queue.canStart,
          blockedReason: queue.blockedReason,
          manuallyPaused: queue.manuallyPaused,
          manualPauseMode: queue.manualPauseMode,
          waitingReason: queue.waitingReason,
          runningJobs: queue.runningJobs,
          queued: stats?.queued ?? 0,
        })
      : null,
  )

  // Kept so the sidebar's live indicator and the Queue view stay in step with this page.
  let _live = $derived(activity.activeJobs)
</script>

<header class="mb-6">
  <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">{i18n.m.nav.dashboard}</h1>
  <p class="text-sm text-slate-500 dark:text-slate-400">{i18n.m.dashboard.subtitle}</p>
</header>

{#if error}
  <Banner kind="error" class="mb-6">{error}</Banner>
{/if}

<DashboardStatusBar
  state={queueState}
  freeDiskBytes={queue?.freeDiskBytes ?? null}
  workRoot={queue?.workRoot ?? ''}
  maxConcurrent={queue?.maxConcurrentJobs ?? null}
/>

<InFlightPanel jobs={runningJobs} state={queueState} />

<div class="mb-4 grid gap-4 lg:grid-cols-2">
  <FleetPanel {workers} {workersAvailable} {runningLocally} />
  <NeedsYouPanel {stats} {failures} />
</div>

<TelemetryRail
  {stats}
  {healthy}
  {healthDetail}
  bind:confirmingReset
  {resetting}
  onreset={resetSavings}
/>
