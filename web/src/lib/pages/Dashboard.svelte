<script lang="ts">
  import {
    api,
    type DailySaving,
    type FailureGroup,
    type OptimisationResult,
    type Health,
    type Job,
    type QueueStatus,
    type Stats,
    type ToolCheck,
    type Worker,
  } from '../api'
  import { dashboardState } from '../dashboard-state'
  import { i18n, t } from '../i18n/i18n.svelte'
  import Banner from '../components/Banner.svelte'
  import FleetPanel from '../components/FleetPanel.svelte'
  import InFlightPanel from '../components/InFlightPanel.svelte'
  import NeedsYouPanel from '../components/NeedsYouPanel.svelte'
  import RecentResultsPanel from '../components/RecentResultsPanel.svelte'
  import SavedPerDayPanel from '../components/SavedPerDayPanel.svelte'
  import SavingsPanel from '../components/SavingsPanel.svelte'

  let health = $state<Health | null>(null)
  let tools = $state<ToolCheck[]>([])
  let stats = $state<Stats | null>(null)
  let queue = $state<QueueStatus | null>(null)
  let jobs = $state<Job[]>([])
  let failures = $state<FailureGroup[]>([])
  let workers = $state<Worker[]>([])
  let workersAvailable = $state(false)
  let results = $state<OptimisationResult[] | null>(null)
  let dailySavings = $state<DailySaving[] | null>(null)
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
    try {
      // These five answer the page. A failure in any of them is a real error worth a banner,
      // because the status bar would otherwise sit there stating a stale state confidently.
      const [healthResult, toolsResult, statsResult, queueResult, jobsResult] = await Promise.all([
        api.health(),
        api.tools(),
        api.stats(),
        api.queueStatus(),
        api.liveJobs(),
      ])
      health = healthResult
      tools = toolsResult
      stats = statsResult
      queue = queueResult
      jobs = jobsResult
      error = null

      // These only enrich: a missing failure breakdown costs a subtitle, an unreachable worker
      // list costs the sidecar rows, and the history panels keep their last answer. None is
      // worth losing the page over.
      const [failuresResult, resultsResult, dailyResult] = await Promise.allSettled([
        api.jobFailures(),
        api.results(12),
        api.dailySavings(30),
      ])
      failures = failuresResult.status === 'fulfilled' ? failuresResult.value : []
      if (resultsResult.status === 'fulfilled') results = resultsResult.value
      if (dailyResult.status === 'fulfilled') dailySavings = dailyResult.value
      await loadFleet()
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.dashboard.error_load
    }
  }

  // Remote workers are opt-in and only exist when the server was started with the experimental
  // switch on. Every worker route answers 403 otherwise, so the page asks the settings first and
  // simply shows this server alone when the answer is no.
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

  // The server decides what counts as in progress and this page does not re-derive it. An
  // earlier attempt to infer it from startedAt/finishedAt disagreed with the server on real
  // data — it found one job where the server counted twenty-four.
  let runningLocally = $derived(jobs.filter((job) => !job.workerName).length)

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
</script>

<header class="mb-6">
  <h1 class="page-title">{i18n.m.nav.dashboard}</h1>
  <p class="page-subtitle">{i18n.m.dashboard.subtitle}</p>
</header>

{#if error}
  <Banner kind="error" class="mb-6">{error}</Banner>
{/if}

<!-- One grid that reorganises by the width it is given, not by the window: a phone stacks, a
     laptop pairs panels, and a large display reads like a control room. -->
<div class="dashboard">
  <div class="dashboard-grid">
    <div class="area-savings">
      <SavingsPanel {stats} {healthy} {healthDetail} bind:confirmingReset {resetting} onreset={resetSavings} />
    </div>
    <div class="area-inflight"><InFlightPanel {jobs} state={queueState} /></div>
    <div class="area-needs"><NeedsYouPanel {stats} {failures} /></div>
    <div class="area-chart"><SavedPerDayPanel days={dailySavings} /></div>
    <div class="area-fleet"><FleetPanel {workers} {workersAvailable} {runningLocally} /></div>
    <div class="area-recent"><RecentResultsPanel {results} /></div>
  </div>
</div>

<style>
  .dashboard { container-type: inline-size; }
  .dashboard-grid {
    display: grid;
    gap: 1rem;
    grid-template-columns: minmax(0, 1fr);
    grid-template-areas: 'inflight' 'needs' 'savings' 'recent' 'chart' 'fleet';
  }
  .area-savings { grid-area: savings; }
  .area-inflight { grid-area: inflight; }
  .area-needs { grid-area: needs; }
  .area-chart { grid-area: chart; }
  .area-fleet { grid-area: fleet; }
  .area-recent { grid-area: recent; }
  .dashboard-grid > div { min-width: 0; }

  @container (min-width: 48rem) {
    .dashboard-grid {
      grid-template-columns: repeat(2, minmax(0, 1fr));
      grid-template-areas:
        'savings inflight'
        'needs fleet'
        'recent recent'
        'chart chart';
    }
  }

  @container (min-width: 100rem) {
    .dashboard-grid {
      gap: 1.25rem;
      grid-template-columns: repeat(12, minmax(0, 1fr));
      grid-template-areas:
        's s s s i i i i i n n n'
        'c c c c c c c c f f f f'
        'r r r r r r r r r r r r';
    }
    .area-savings { grid-area: s; }
    .area-inflight { grid-area: i; }
    .area-needs { grid-area: n; }
    .area-chart { grid-area: c; }
    .area-fleet { grid-area: f; }
    .area-recent { grid-area: r; }
  }
</style>
